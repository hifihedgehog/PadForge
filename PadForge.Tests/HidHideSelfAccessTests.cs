using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// PadForge never hides a device from itself (#484). A Wii Remote
    /// dragged onto a virtual controller was hidden with HidHide, and SDL's
    /// next enumeration could not open it, because HidHide did not count
    /// PadForge as whitelisted. The remote stayed dead until it was removed
    /// from Windows and paired again.
    ///
    /// <para>These run the real ApplyDeviceHiding against a fake driver.
    /// Refused, PadForge takes the hide back, says so and stops hiding until
    /// HidHide's whitelist changes or its inverse application cloak goes off.
    /// Allowed, the hide stays and the probe runs once, and once more when a
    /// controller the release reconnected comes back. With HidHide's inverse
    /// application cloak on, PadForge keeps itself off the list and hides
    /// nothing.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class HidHideSelfAccessTests : IDisposable
    {
        private const uint IOCTL_GET_WHITELIST = 0x80016000;
        private const uint IOCTL_SET_WHITELIST = 0x80016004;
        private const uint IOCTL_GET_BLACKLIST = 0x80016008;
        private const uint IOCTL_SET_BLACKLIST = 0x8001600C;
        private const uint IOCTL_GET_ACTIVE = 0x80016010;
        private const uint IOCTL_SET_ACTIVE = 0x80016014;
        private const uint IOCTL_GET_WLINVERSE = 0x80016018;
        private const int ERROR_ACCESS_DENIED = 5;

        // A Bluetooth Classic remote's HID interface, the shape of the
        // reporter's, with a VID and PID no real device carries so the
        // sweep's present-node lookups find nothing on the bench.
        private const string RemotePath =
            @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002f00d_PID&0484#8&204ddce7&d&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private static readonly string RemoteId = HidHideController.DevicePathToInstanceId(RemotePath);

        /// <summary>The control device, as HidHide answers it: a list read
        /// is a size probe and then an exact read, a list write replaces the
        /// whole list, and the inverse flag is one byte (Logic.c
        /// OnControlDeviceIoGetInverse).</summary>
        private sealed class FakeDriver
        {
            public readonly List<string> Blacklist = new();
            public readonly List<string> Whitelist = new();
            public readonly List<List<string>> BlacklistWrites = new();
            public int WhitelistWrites;
            public bool Active;
            public bool Inverse;
            public bool InverseUnsupported;
            public bool RefuseWhitelistWrites;
            public bool RefuseWhitelistReads;
            public bool RefuseBlacklistReads;

            private static (bool, int) Read(List<string> list, byte[] outBuf)
            {
                var sb = new StringBuilder();
                foreach (var s in list) { sb.Append(s); sb.Append('\0'); }
                sb.Append('\0');
                var bytes = Encoding.Unicode.GetBytes(sb.ToString());
                if (outBuf == null || outBuf.Length == 0) return (true, bytes.Length);
                if (outBuf.Length < bytes.Length) return (false, 0);
                Array.Copy(bytes, outBuf, bytes.Length);
                return (true, bytes.Length);
            }

            private static List<string> Parse(byte[] inBuf)
            {
                var parsed = new List<string>();
                if (inBuf != null && inBuf.Length > 0)
                    foreach (var e in Encoding.Unicode.GetString(inBuf).Split('\0'))
                        if (e.Length > 0) parsed.Add(e);
                return parsed;
            }

            public (bool ok, int bytes) Io(uint ioctl, byte[] inBuf, byte[] outBuf)
            {
                switch (ioctl)
                {
                    case IOCTL_GET_BLACKLIST:
                        return RefuseBlacklistReads ? (false, 0) : Read(Blacklist, outBuf);
                    case IOCTL_GET_WHITELIST:
                        return RefuseWhitelistReads ? (false, 0) : Read(Whitelist, outBuf);
                    case IOCTL_SET_BLACKLIST:
                    {
                        var parsed = Parse(inBuf);
                        BlacklistWrites.Add(parsed);
                        Blacklist.Clear();
                        Blacklist.AddRange(parsed);
                        return (true, inBuf?.Length ?? 0);
                    }
                    case IOCTL_SET_WHITELIST:
                    {
                        if (RefuseWhitelistWrites) return (false, 0);
                        var parsed = Parse(inBuf);
                        WhitelistWrites++;
                        Whitelist.Clear();
                        Whitelist.AddRange(parsed);
                        return (true, inBuf?.Length ?? 0);
                    }
                    case IOCTL_GET_ACTIVE:
                        outBuf[0] = Active ? (byte)1 : (byte)0;
                        return (true, 1);
                    case IOCTL_SET_ACTIVE:
                        Active = inBuf[0] != 0;
                        return (true, 0);
                    case IOCTL_GET_WLINVERSE:
                        if (InverseUnsupported) return (false, 0);
                        outBuf[0] = Inverse ? (byte)1 : (byte)0;
                        return (true, 1);
                    default:
                        return (false, 0);
                }
            }
        }

        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly FakeDriver _driver = new();
        private readonly List<string> _opened = new();
        private readonly MainViewModel _vm;
        private readonly InputService _svc;
        private (bool opened, int error) _openAnswer = (true, 0);
        private string _interface = RemotePath;
        // When Windows says the remote last arrived. Later than any cycle
        // unless a test says the remote is still on its old device stack.
        private DateTime? _arrival = DateTime.MaxValue;

        public HidHideSelfAccessTests()
        {
            HidHideController.ResetManagedForTests();
            HidHideController.IoSeam = _driver.Io;
            HidHideController.InterfaceSeam = id =>
                string.Equals(id, RemoteId, StringComparison.OrdinalIgnoreCase) ? _interface : null;
            HidHideController.OpenSeam = path => { _opened.Add(path); return _openAnswer; };
            HidHideController.SerialReader = _ => null;
            HidHideController.ArrivalSeam = _ => _arrival;

            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "Nintendo Wii Remote",
                VendorId = 0xF00D,
                ProdId = 0x0484,
                DevicePath = RemotePath,
                SerialNumber = "0022aaccdee8",
                HidHideEnabled = true,
                IsOnline = true,
            });

            _vm = new MainViewModel();
            _vm.Settings.EnableInputHiding = true;
            _svc = new InputService(_vm);
            _svc.ControllerRelease.Resolve = (id, _, _) =>
            {
                lock (_released) _released.Add(id);
                return (null, "test");
            };
            _svc.ControllerRelease.Log = _ => { };
        }

        private readonly List<string> _released = new();

        private string[] Released()
        {
            _svc.ControllerRelease.Pending.Wait(TimeSpan.FromSeconds(10));
            lock (_released) return _released.ToArray();
        }

        public void Dispose()
        {
            _svc.Dispose();
            // A batch the release still has in hand reads the fake driver,
            // so the fake stays until that batch is done.
            _svc.ControllerRelease.Pending.Wait(TimeSpan.FromSeconds(30));
            HidHideController.IoSeam = null;
            HidHideController.ControlOpenSeam = null;
            HidHideController.InterfaceSeam = null;
            HidHideController.OpenSeam = null;
            HidHideController.AnnounceSeam = null;
            HidHideController.SerialReader = null;
            HidHideController.ArrivalSeam = null;
            HidHideController.ChainSeam = null;
            HidHideController.PresenceSeam = null;
            HidHideController.TreeSeam = null;
            HidHideController.OwnerChainSeam = null;
            HidHideController.PersonaScopeSeam = null;
            HidHideController.PresentHidNodesSeam = null;
            HidHideController.XusbInterfaceSeam = null;
            HidHideController.ReleaseWalkSeam = null;
            HidHideController.ResetManagedForTests();
            SettingsManager.UserDevices = _savedDevices;
        }

        private static bool Names(IEnumerable<string> list, string id)
            => list.Contains(id, StringComparer.OrdinalIgnoreCase);

        /// <summary>Writes a line no other test writes to the diagnostics
        /// ring and returns it. The ring is shared by the whole run, so a
        /// test reads only what follows its own mark.</summary>
        private static string Mark()
        {
            string mark = "HidHideSelfAccessTests mark " + Guid.NewGuid().ToString("N");
            SdlDiagLog.WriteLine(mark);
            return mark;
        }

        /// <summary>The ring's lines after the mark that carry the text, line
        /// ends trimmed.</summary>
        private static string[] LogLinesAfter(string mark, string text)
        {
            var lines = SdlDiagLog.Snapshot().Split('\n').Select(l => l.TrimEnd()).ToList();
            int at = lines.FindIndex(l => l.EndsWith(mark, StringComparison.Ordinal));
            Assert.True(at >= 0, "the mark rolled out of the diagnostics ring");
            return lines.Skip(at + 1).Where(l => l.Contains(text, StringComparison.Ordinal)).ToArray();
        }

        /// <summary>The report's case. HidHide refuses PadForge's own open
        /// of the remote it just hid, the answer it gives a process it does
        /// not count as whitelisted (Logic.c OnDeviceFileCreate). PadForge
        /// takes the hide back, says why, and hides nothing again while the
        /// whitelist stays as it was.</summary>
        [Fact]
        public void ARefusedOpenTakesTheHideBackAndStopsHiding()
        {
            _openAnswer = (false, ERROR_ACCESS_DENIED);

            _svc.ApplyDeviceHiding();

            Assert.Contains(_driver.BlacklistWrites, w => Names(w, RemoteId));
            Assert.False(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(new[] { RemotePath }, _opened);
            Assert.Equal(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);

            int writes = _driver.BlacklistWrites.Count;
            _svc.ApplyDeviceHiding();
            Assert.DoesNotContain(_driver.BlacklistWrites.Skip(writes), w => Names(w, RemoteId));
            Assert.False(Names(_driver.Blacklist, RemoteId));
        }

        /// <summary>The report's order: nothing is hidden at launch, and the
        /// refusal comes on a later apply. Taking the hide back leaves the
        /// same empty set the first block printed, and the REFUSED line still
        /// reaches the log, with PadForge's native path, the fact that the
        /// driver's list held it, and that no announcement was made (a test
        /// host maps no image).</summary>
        [Fact]
        public void ARefusalOnALaterApplyStillReachesTheLog()
        {
            var remote = SettingsManager.UserDevices.Items[0];
            remote.HidHideEnabled = false;
            _svc.ApplyDeviceHiding();

            remote.HidHideEnabled = true;
            _openAnswer = (false, ERROR_ACCESS_DENIED);
            string mark = Mark();
            _svc.ApplyDeviceHiding();

            string native = HidHideController.CurrentProcessNativeImagePath();
            string line = Assert.Single(LogLinesAfter(mark, "HIDHIDE REFUSED"));
            Assert.Contains($"open {RemotePath} (err=5)", line);
            Assert.EndsWith($" announced=no native={native} listed=yes", line);
        }

        /// <summary>A whitelist the driver will not write or read is logged
        /// and holds new hides back, since a device hidden then can be hidden
        /// from PadForge itself. Nothing is probed and nothing is held over,
        /// and the apply after the list takes PadForge's entry hides the
        /// remote.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AWhitelistTheDriverWillNotTakeHoldsNewHidesBack(bool unreadable)
        {
            _driver.RefuseWhitelistWrites = !unreadable;
            _driver.RefuseWhitelistReads = unreadable;
            string mark = Mark();

            _svc.ApplyDeviceHiding();

            Assert.Single(LogLinesAfter(mark, "HIDHIDE whitelist could not be read or written"));
            Assert.DoesNotContain(_driver.BlacklistWrites, w => Names(w, RemoteId));
            Assert.Empty(_opened);
            Assert.NotEqual(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);

            _driver.RefuseWhitelistWrites = false;
            _driver.RefuseWhitelistReads = false;
            _svc.ApplyDeviceHiding();

            Assert.True(Names(_driver.Whitelist, HidHideController.CurrentProcessNativeImagePath()));
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.Single(_opened);
        }

        /// <summary>The refusal says what the list held when it came. Gone
        /// from the list means another program rewrote it after PadForge's
        /// write. Listed and refused means the driver does not know this
        /// process by that path.</summary>
        [Theory]
        [InlineData(false, "no")]
        [InlineData(true, "unreadable")]
        public void ARefusalSaysWhatTheListHeld(bool unreadable, string listed)
        {
            HidHideController.OpenSeam = path =>
            {
                _opened.Add(path);
                if (unreadable) _driver.RefuseWhitelistReads = true;
                else _driver.Whitelist.Clear();
                return (false, ERROR_ACCESS_DENIED);
            };
            string mark = Mark();

            _svc.ApplyDeviceHiding();

            Assert.EndsWith(" listed=" + listed, Assert.Single(LogLinesAfter(mark, "HIDHIDE REFUSED")));
        }

        /// <summary>HidHide keeps its verdict on a process until a whitelist
        /// write clears it (Config.c evaluationCache, Logic.c SetWhitelist).
        /// A list that changed after the refusal, here by another program
        /// adding an entry, lets PadForge hide again and probe again, and the
        /// refusal message goes away.</summary>
        [Fact]
        public void AChangedWhitelistLetsPadForgeHideAgain()
        {
            _openAnswer = (false, ERROR_ACCESS_DENIED);
            _svc.ApplyDeviceHiding();
            Assert.False(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);

            _openAnswer = (true, 0);
            _svc.ApplyDeviceHiding();
            Assert.False(Names(_driver.Blacklist, RemoteId));
            Assert.Single(_opened);

            _driver.Whitelist.Add(@"\Device\HarddiskVolume9\Games\other.exe");
            string mark = Mark();
            _svc.ApplyDeviceHiding();

            Assert.Single(LogLinesAfter(mark, "HIDHIDE whitelist changed since HidHide refused PadForge"));
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(2, _opened.Count);
            Assert.Equal(string.Empty, _vm.StatusText);
        }

        // ── A driver that started while PadForge ran ──

        /// <summary>The fake driver's memory of this process, as HidHide
        /// keeps it. A process is unknown until an image is mapped into it
        /// while the driver listens, and an unknown process is refused at a
        /// hidden device whatever the list holds (Config.c
        /// HidHideProcessIdRegister,
        /// HidHideProcessIdCheckFullImageNameAgainstWhitelist). The driver
        /// listens from the announcement numbered
        /// <paramref name="listensFrom"/> on, and every open answers by
        /// what it knows. Returns the count of announcements.</summary>
        private Func<int> ADriverThatLearnsPadForgeFromItsImage(int listensFrom)
        {
            int announced = 0;
            bool known = false;
            HidHideController.AnnounceSeam = () =>
            {
                if (++announced >= listensFrom) known = true;
                return true;
            };
            HidHideController.OpenSeam = path =>
            {
                _opened.Add(path);
                return known ? (true, 0) : (false, ERROR_ACCESS_DENIED);
            };
            return () => announced;
        }

        /// <summary>The report of 2026-11-19: PadForge installed HidHide on
        /// a PC that had never had it, and the controller it was set to hide
        /// stayed visible. The driver had started while PadForge ran, and
        /// such a driver has no name for PadForge and refuses it. The first
        /// apply that finds the driver announces PadForge before it writes
        /// either list, so the hide stays and the open works the first
        /// time. One announcement serves the applies that follow.</summary>
        [Fact]
        public void TheFirstApplyThatFindsTheDriverAnnouncesPadForgeBeforeItHides()
        {
            var announced = ADriverThatLearnsPadForgeFromItsImage(listensFrom: 1);
            var learn = HidHideController.AnnounceSeam;
            var writesBefore = new List<int>();
            HidHideController.AnnounceSeam = () =>
            {
                writesBefore.Add(_driver.BlacklistWrites.Count + _driver.WhitelistWrites);
                return learn();
            };

            _svc.ApplyDeviceHiding();

            Assert.Equal(new[] { 0 }, writesBefore);
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(new[] { RemotePath }, _opened);
            Assert.NotEqual(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);

            _svc.ApplyDeviceHiding();
            Assert.Equal(1, announced());
        }

        /// <summary>An apply that finds no driver forgets the announcement.
        /// A driver that comes back is new to every running process, so the
        /// next apply that finds it announces PadForge again.</summary>
        [Fact]
        public void ADriverThatComesBackIsToldAgain()
        {
            var announced = ADriverThatLearnsPadForgeFromItsImage(listensFrom: 1);
            _svc.ApplyDeviceHiding();
            Assert.Equal(1, announced());

            HidHideController.ControlOpenSeam = () => 2;
            _svc.ApplyDeviceHiding();
            Assert.Equal(1, announced());

            HidHideController.ControlOpenSeam = null;
            _svc.ApplyDeviceHiding();
            Assert.Equal(2, announced());
        }

        /// <summary>An announcement that mapped no image is made again at
        /// each apply until one maps it, and not after.</summary>
        [Fact]
        public void AnAnnouncementThatMappedNothingIsMadeAgain()
        {
            int tried = 0;
            HidHideController.AnnounceSeam = () => ++tried >= 3;

            for (int apply = 0; apply < 5; apply++)
                _svc.ApplyDeviceHiding();

            Assert.Equal(3, tried);
        }

        /// <summary>The driver's control device opens a moment before the
        /// driver listens for images (Logic.c OnDriverCreate makes the
        /// device, then registers the callback), so the announcement an
        /// apply makes on first finding the driver can come too early. The
        /// refusal that follows is answered with a second announcement and
        /// a second open of the same interface. The hide stays, its
        /// controller goes to the release, and nothing says PadForge was
        /// refused.</summary>
        [Fact]
        public void ARefusalIsAnsweredWithAnAnnouncementAndASecondOpen()
        {
            var announced = ADriverThatLearnsPadForgeFromItsImage(listensFrom: 2);
            string mark = Mark();

            _svc.ApplyDeviceHiding();

            Assert.Equal(2, announced());
            Assert.Equal(new[] { RemotePath, RemotePath }, _opened);
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.NotEqual(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);
            Assert.Empty(LogLinesAfter(mark, "HIDHIDE REFUSED"));
            Assert.Single(LogLinesAfter(mark, $"HIDHIDE reach ok once HidHide had PadForge's image name: PadForge opened {RemotePath}"));
            Assert.Contains(RemoteId, Released(), StringComparer.OrdinalIgnoreCase);

            _svc.ApplyDeviceHiding();
            Assert.Equal(2, announced());
            Assert.Equal(2, _opened.Count);
        }

        /// <summary>A process the driver knows by another image's name is
        /// refused whatever it announces, since the driver keeps the first
        /// name it heard for a process. PadForge announces, opens a second
        /// time, takes the hide back, and the log says the announcement was
        /// made.</summary>
        [Fact]
        public void ARefusalThatOutlastsTheAnnouncementTakesTheHideBack()
        {
            int announced = 0;
            HidHideController.AnnounceSeam = () => { announced++; return true; };
            _openAnswer = (false, ERROR_ACCESS_DENIED);
            string mark = Mark();

            _svc.ApplyDeviceHiding();

            // Once on finding the driver, once on the refusal.
            Assert.Equal(2, announced);
            Assert.Equal(new[] { RemotePath, RemotePath }, _opened);
            Assert.False(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);
            string native = HidHideController.CurrentProcessNativeImagePath();
            Assert.EndsWith($" announced=yes native={native} listed=yes",
                Assert.Single(LogLinesAfter(mark, "HIDHIDE REFUSED")));
            Assert.Empty(Released());
        }

        /// <summary>An install is announced while it runs and once more when
        /// it has returned, by which time the driver's start is over.
        /// Nothing is announced after that.</summary>
        [Fact]
        public void AnInstallIsAnnouncedWhileItRunsAndOnceMoreWhenItIsOver()
        {
            int announced = 0, afterTheInstall = 0;
            bool installOver = false;
            HidHideController.AnnounceSeam = () =>
            {
                System.Threading.Interlocked.Increment(ref announced);
                if (System.Threading.Volatile.Read(ref installOver))
                    System.Threading.Interlocked.Increment(ref afterTheInstall);
                return true;
            };

            HidHideController.AnnounceThisProcessWhile(() =>
            {
                Assert.True(System.Threading.SpinWait.SpinUntil(
                    () => System.Threading.Volatile.Read(ref announced) >= 3, TimeSpan.FromSeconds(30)),
                    "the install was not announced while it ran");
                System.Threading.Volatile.Write(ref installOver, true);
            });

            Assert.True(System.Threading.Volatile.Read(ref afterTheInstall) >= 1,
                "no announcement followed the install");
            int atReturn = System.Threading.Volatile.Read(ref announced);
            System.Threading.Thread.Sleep(HidHideController.AnnounceEvery * 5);
            Assert.Equal(atReturn, System.Threading.Volatile.Read(ref announced));
        }

        /// <summary>An install that throws is announced the same way, since
        /// a driver that loaded before a later step failed has still
        /// started, and its fault reaches the caller.</summary>
        [Fact]
        public void AnInstallThatThrowsIsAnnouncedAllTheSameAndItsFaultGoesOn()
        {
            int announced = 0, afterTheFault = 0;
            bool thrown = false;
            HidHideController.AnnounceSeam = () =>
            {
                System.Threading.Interlocked.Increment(ref announced);
                if (System.Threading.Volatile.Read(ref thrown))
                    System.Threading.Interlocked.Increment(ref afterTheFault);
                return true;
            };

            var fault = Assert.Throws<InvalidOperationException>(() =>
                HidHideController.AnnounceThisProcessWhile(() =>
                {
                    // Once the loop has run, so that what follows the fault
                    // is the announcement made after it and not the loop's
                    // first.
                    Assert.True(System.Threading.SpinWait.SpinUntil(
                        () => System.Threading.Volatile.Read(ref announced) >= 2, TimeSpan.FromSeconds(30)),
                        "the install was not announced while it ran");
                    System.Threading.Volatile.Write(ref thrown, true);
                    throw new InvalidOperationException("the installer failed");
                }));

            Assert.Equal("the installer failed", fault.Message);
            Assert.True(System.Threading.Volatile.Read(ref afterTheFault) >= 1);
            int atReturn = System.Threading.Volatile.Read(ref announced);
            System.Threading.Thread.Sleep(HidHideController.AnnounceEvery * 5);
            Assert.Equal(atReturn, System.Threading.Volatile.Read(ref announced));
        }

        /// <summary>What makes the announcement one the driver hears, and
        /// what puts every install inside it, read from the source. The
        /// mapping is an image mapping with SEC_IMAGE: Windows keeps a
        /// SEC_IMAGE_NO_EXECUTE mapping from the image-load callbacks
        /// (CreateFileMapping's documentation, and both traces of
        /// 2026-11-19), so that flag would announce nothing while every
        /// call still succeeded. HidHide's install has one way in, through
        /// the wrapper that announces.</summary>
        [Fact]
        public void TheAnnouncementIsAnImageMapping_AndEveryInstallRunsInsideIt()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);

            string ctl = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Common", "HidHideController.cs"));
            Assert.Contains("private const uint SEC_IMAGE = 0x01000000;", ctl);
            Assert.DoesNotContain("0x11000000", ctl);
            string announce = FenceSource.Body(ctl, "internal static bool AnnounceThisProcess()");
            Assert.Contains("CreateFileMappingW(file, IntPtr.Zero, PAGE_READONLY | SEC_IMAGE, 0, 0, null);", announce);
            Assert.Contains("MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, UIntPtr.Zero);", announce);
            Assert.Contains(@"found.Add(@""\\?\GLOBALROOT"" + native);", announce);

            string installer = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Common", "DriverInstaller.cs"));
            // The install runs inside the announcing wrapper, and PadForge's
            // own install then forgets what it read of HidHide's presence.
            string install = FenceSource.Body(installer, "public static void InstallHidHide()");
            Assert.Contains("try { HidHideController.AnnounceThisProcessWhile(InstallHidHideNow); }", install);
            Assert.Contains("finally { s_hidHideReads.Forget(); }", install);
            // The wrapper's argument and the declaration: nothing else runs
            // the install.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(installer, @"\bInstallHidHideNow\b").Count);
            Assert.Contains("HidHideArm64Installer.Install();",
                FenceSource.Body(installer, "private static void InstallHidHideNow()"));
            Assert.Equal(new[] { "PadForge.App/Common/DriverInstaller.cs" },
                FenceSource.Census(@"\bHidHideArm64Installer\.Install\(\)").Keys.ToArray());
            Assert.Equal(new[] { "PadForge.App/Common/DriverInstaller.cs" },
                FenceSource.Census(@"\bInstallHidHideNow\b").Keys.ToArray());
        }

        /// <summary>A hide PadForge can still reach hands what it added to
        /// the release, which takes a USB controller from programs that
        /// opened it before the hide. An apply that adds nothing hands over
        /// nothing.</summary>
        [Fact]
        public void AReachableHideReleasesWhatItAdded()
        {
            _svc.ApplyDeviceHiding();
            Assert.Contains(RemoteId, Released(), StringComparer.OrdinalIgnoreCase);

            int before = Released().Length;
            _svc.ApplyDeviceHiding();
            Assert.Equal(before, Released().Length);
        }

        /// <summary>A probe nothing answered has not shown that PadForge can
        /// reopen what it hid, so nothing is released yet.</summary>
        [Fact]
        public void AnUnconfirmedHideReleasesNothing()
        {
            _interface = null;
            _svc.ApplyDeviceHiding();
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.Empty(Released());
        }

        /// <summary>A tablet's collection has its own capture path, so the
        /// release leaves it out while it takes the controller hidden in the
        /// same apply.</summary>
        [Fact]
        public void ATabletIsLeftToItsOwnCapture()
        {
            const string tabletPath = @"\\?\HID#VID_F00D&PID_7AB1#9&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            string tabletId = HidHideController.DevicePathToInstanceId(tabletPath);
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "Pen Tablet",
                DevicePath = tabletPath,
                CapType = InputDeviceType.Tablet,
                HidHideEnabled = true,
                IsOnline = true,
            });

            _svc.ApplyDeviceHiding();

            Assert.True(Names(_driver.Blacklist, tabletId));
            Assert.Contains(RemoteId, Released(), StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(tabletId, Released(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A refused hide was taken back, so it releases
        /// nothing.</summary>
        [Fact]
        public void ARefusedHideReleasesNothing()
        {
            _openAnswer = (false, ERROR_ACCESS_DENIED);
            _svc.ApplyDeviceHiding();
            Assert.Empty(Released());
        }

        /// <summary>Reach confirmed by an earlier pass does not carry a later
        /// refusal through: the ids that apply took back are not
        /// released.</summary>
        [Fact]
        public void ARefusalAfterAnEarlierPassReleasesNothing()
        {
            _svc.ApplyDeviceHiding();
            int before = Released().Length;

            _driver.Blacklist.Clear();
            _openAnswer = (false, ERROR_ACCESS_DENIED);
            _svc.ApplyDeviceHiding();

            Assert.False(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(before, Released().Length);
        }

        /// <summary>A hide held back for an unconfirmed whitelist added
        /// nothing, so it releases nothing.</summary>
        [Fact]
        public void AHeldBackHideReleasesNothing()
        {
            _driver.RefuseWhitelistReads = true;
            _svc.ApplyDeviceHiding();
            Assert.Empty(Released());
        }

        /// <summary>A sync that holds new hides back adds nothing and still
        /// removes what PadForge no longer wants, leaves other programs'
        /// entries alone, and claims only the ids the driver carries, so an
        /// id another program hides later stays that program's.</summary>
        [Fact]
        public void AHeldBackSyncAddsNothingAndStillRemoves()
        {
            const string kept = @"HID\VID_F00D&PID_0001\1";
            const string dropped = @"HID\VID_F00D&PID_0002\1";
            const string heldBack = @"HID\VID_F00D&PID_0003\1";
            const string other = @"HID\VID_F00D&PID_0004\1";
            _driver.Blacklist.Add(other);
            Assert.True(HidHideController.SyncManagedDevices(new HashSet<string> { kept, dropped }, out _, out _));

            Assert.True(HidHideController.SyncManagedDevices(new HashSet<string> { kept, heldBack },
                out var added, out var removed, allowAdditions: false));
            Assert.Empty(added);
            Assert.Equal(new[] { dropped }, removed);
            Assert.Equal(new[] { other, kept }, _driver.Blacklist);

            _driver.Blacklist.Add(heldBack);
            Assert.True(HidHideController.SyncManagedDevices(new HashSet<string>(), out _, out removed));
            Assert.Equal(new[] { kept }, removed);
            Assert.Equal(new[] { other, heldBack }, _driver.Blacklist);
        }

        /// <summary>Two reads of the list match when they hold the same
        /// entries in any order and case, and a failed read matches
        /// nothing.</summary>
        [Fact]
        public void TwoReadsOfTheWhitelistMatchByTheirEntries()
        {
            var a = new List<string> { @"\Device\HarddiskVolume3\A.exe", @"\Device\HarddiskVolume3\B.exe" };
            Assert.True(InputService.SameWhitelist(a, new List<string> { @"\DEVICE\HARDDISKVOLUME3\b.EXE", @"\Device\HarddiskVolume3\A.exe" }));
            Assert.False(InputService.SameWhitelist(a, new List<string> { @"\Device\HarddiskVolume3\A.exe" }));
            Assert.False(InputService.SameWhitelist(a, null));
            Assert.False(InputService.SameWhitelist(null, null));
        }

        /// <summary>A whitelist fault counts as trouble, so it reaches the
        /// log on an apply that changes nothing else.</summary>
        [Fact]
        public void AWhitelistFaultOnAnApplyThatChangesNothingStillPrints()
        {
            _svc.ApplyDeviceHiding();
            _driver.RefuseWhitelistReads = true;
            string mark = Mark();

            _svc.ApplyDeviceHiding();

            Assert.Single(LogLinesAfter(mark, "HIDHIDE whitelist could not be read or written"));
        }

        /// <summary>The ordinary case: PadForge passes the gate, the hide
        /// stays, and an apply that hides nothing new does not probe
        /// again.</summary>
        [Fact]
        public void AnAllowedOpenKeepsTheHideAndProbesOnce()
        {
            _svc.ApplyDeviceHiding();

            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.True(_driver.Active);
            Assert.Single(_opened);
            Assert.NotEqual(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);

            _svc.ApplyDeviceHiding();
            Assert.Single(_opened);
            Assert.True(Names(_driver.Blacklist, RemoteId));
        }

        /// <summary>A probe nothing answers decides nothing: the hide stays,
        /// and the next apply asks again although it adds nothing.</summary>
        [Fact]
        public void AnUnansweredProbeKeepsTheHideAndAsksAgain()
        {
            _interface = null;
            _svc.ApplyDeviceHiding();
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.Empty(_opened);

            _interface = RemotePath;
            _svc.ApplyDeviceHiding();
            Assert.Single(_opened);
        }

        /// <summary>The driver matches a process by the native path of its
        /// image (Config.c HidHideProcessIdCheckFullImageNameAgainstWhitelist),
        /// and PadForge lists itself in exactly that form.</summary>
        [Fact]
        public void PadForgeListsItselfByTheNativePathTheDriverMatches()
        {
            _svc.ApplyDeviceHiding();

            string native = HidHideController.CurrentProcessNativeImagePath();
            Assert.False(string.IsNullOrEmpty(native));
            Assert.True(Names(_driver.Whitelist, native));
        }

        /// <summary>A folder behind a junction, on a SUBST drive or on a
        /// volume mounted inside another drive converts to one path while the
        /// kernel names the image by another. Both go on the list, the native
        /// one as it is, and a path that does not convert is left off.</summary>
        [Fact]
        public void TheWhitelistCarriesTheNativePathBesideTheConvertedOne()
        {
            var entries = InputService.DesiredWhitelistEntries(
                new[] { @"X:\PadForge\PadForge.exe", @"C:\Tools\unmounted.exe" },
                @"\Device\HarddiskVolume7\PadForge\PadForge.exe",
                p => p.StartsWith(@"X:\") ? @"\??\C:\Games\PadForge\PadForge.exe" : null);

            Assert.True(entries.SetEquals(new[]
            {
                @"\??\C:\Games\PadForge\PadForge.exe",
                @"\Device\HarddiskVolume7\PadForge\PadForge.exe",
            }));
        }

        /// <summary>With the inverse application cloak on, the list names
        /// the applications HidHide hides devices from (Logic.c Whitelisted
        /// returns the negation). PadForge takes itself off, leaves every
        /// other entry, hides nothing, and says so once.</summary>
        [Fact]
        public void TheInverseCloakKeepsPadForgeOffTheListAndHidesNothing()
        {
            string native = HidHideController.CurrentProcessNativeImagePath();
            const string other = @"\Device\HarddiskVolume9\Games\other.exe";
            _driver.Inverse = true;
            _driver.Whitelist.AddRange(new[] { native, other });

            _svc.ApplyDeviceHiding();

            Assert.Equal(new[] { other }, _driver.Whitelist);
            Assert.DoesNotContain(_driver.BlacklistWrites, w => Names(w, RemoteId));
            Assert.Empty(_opened);
            Assert.Equal(Strings.Instance.Status_HidHideInverse, _vm.StatusText);

            _vm.StatusText = string.Empty;
            _svc.ApplyDeviceHiding();
            Assert.Equal(string.Empty, _vm.StatusText);
        }

        /// <summary>A driver older than the inverse flag rejects the IOCTL,
        /// which reads as an ordinary list.</summary>
        [Fact]
        public void ADriverWithoutTheInverseFlagHidesAsBefore()
        {
            _driver.InverseUnsupported = true;

            _svc.ApplyDeviceHiding();

            Assert.True(Names(_driver.Blacklist, RemoteId));
        }

        /// <summary>The verdict belongs to the process, so the first hidden
        /// HID interface that answers settles it. Parents carry no HID
        /// interface and are never looked up, and a node that is gone or
        /// has no interface is passed over.</summary>
        [Fact]
        public void TheProbeStopsAtTheFirstAnswerAndSkipsTheRest()
        {
            var asked = new List<string>();
            var ids = new[]
            {
                @"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002057E_PID&0306\7&1A6E07AF&0&0022AACCDEE8_C00000000",
                @"HID\NOINTERFACE\1",
                @"HID\GONE\2",
                @"HID\REFUSED\3",
                @"HID\NEVERASKED\4",
            };
            string Interface(string id)
            {
                asked.Add(id);
                return id.Contains("NOINTERFACE") ? null : "path:" + id;
            }
            bool Open(string path, out int error)
            {
                error = path.Contains("GONE") ? 2 : ERROR_ACCESS_DENIED;
                return false;
            }

            bool? reach = InputService.ProbeOwnHidHideReach(ids, Interface, Open, out string probed, out int err);

            Assert.False(reach);
            Assert.Equal(@"path:HID\REFUSED\3", probed);
            Assert.Equal(ERROR_ACCESS_DENIED, err);
            Assert.Equal(new[] { @"HID\NOINTERFACE\1", @"HID\GONE\2", @"HID\REFUSED\3" }, asked);
        }

        [Fact]
        public void TheProbeSaysYesOnAnOpenAndNothingWhenNothingAnswers()
        {
            bool Opens(string path, out int error) { error = 0; return true; }
            bool Gone(string path, out int error) { error = 2; return false; }

            Assert.True(InputService.ProbeOwnHidHideReach(new[] { @"HID\A\1" }, id => "p", Opens, out string probed, out _));
            Assert.Equal("p", probed);
            Assert.Null(InputService.ProbeOwnHidHideReach(new[] { @"HID\A\1" }, id => "p", Gone, out _, out _));
            Assert.Null(InputService.ProbeOwnHidHideReach(new[] { @"USB\VID_045E&PID_028E\1" }, id => "p", Opens, out _, out _));
        }

        /// <summary>Only the named entries come off, matched without regard
        /// to case, and a list that holds none of them is not rewritten.</summary>
        [Fact]
        public void RemovingWhitelistEntriesLeavesTheRest()
        {
            _driver.Whitelist.AddRange(new[] { @"\Device\HarddiskVolume3\A.exe", @"\Device\HarddiskVolume3\B.exe" });

            Assert.True(HidHideController.RemoveWhitelistEntries(new[] { @"\DEVICE\HARDDISKVOLUME3\a.EXE", null }));
            Assert.Equal(new[] { @"\Device\HarddiskVolume3\B.exe" }, _driver.Whitelist);

            _driver.Whitelist.Add(@"\Device\HarddiskVolume3\C.exe");
            int writes = _driver.WhitelistWrites;
            Assert.True(HidHideController.RemoveWhitelistEntries(new[] { @"\Device\HarddiskVolume3\Z.exe" }));
            Assert.Equal(2, _driver.Whitelist.Count);
            Assert.Equal(writes, _driver.WhitelistWrites);
        }

        [Fact]
        public void TheInverseFlagIsReadFromTheDriver()
        {
            Assert.True(HidHideController.TryGetInverse(out bool inverse));
            Assert.False(inverse);
            _driver.Inverse = true;
            Assert.True(HidHideController.TryGetInverse(out inverse));
            Assert.True(inverse);
            _driver.InverseUnsupported = true;
            Assert.False(HidHideController.TryGetInverse(out inverse));
            Assert.False(inverse);
        }

        /// <summary>The three ways into the control device: the availability
        /// check, the probe that carries the error, and an IOCTL.</summary>
        private static void CallControlDevice(int way)
        {
            switch (way)
            {
                case 0: HidHideController.IsAvailable(); break;
                case 1: HidHideController.TryProbe(out _); break;
                default: HidHideController.GetBlacklist(); break;
            }
        }

        /// <summary>One thread of the process talks to the control device at
        /// a time. Every open asks for no sharing, so two threads calling
        /// together would refuse each other, and the release reads the
        /// blacklist from the thread pool when a batch runs while the apply
        /// runs on the UI thread. All three ways in take the one gate. The
        /// first call is held inside the device while the second arrives from
        /// another thread, and the second waits at the gate until the first
        /// has left.</summary>
        [Theory]
        [InlineData(0, 0)]
        [InlineData(0, 1)]
        [InlineData(0, 2)]
        [InlineData(1, 0)]
        [InlineData(1, 1)]
        [InlineData(1, 2)]
        [InlineData(2, 0)]
        [InlineData(2, 1)]
        [InlineData(2, 2)]
        public void ControlDeviceCallsNeverOverlap(int first, int second)
        {
            // Compiled before the measurement, so the second thread meets
            // nothing to wait on ahead of the gate.
            for (int way = 0; way < 3; way++) CallControlDevice(way);

            int inside = 0, most = 0, calls = 0;
            using var entered = new System.Threading.ManualResetEventSlim(false);
            using var leave = new System.Threading.ManualResetEventSlim(false);
            void Inside()
            {
                int now = System.Threading.Interlocked.Increment(ref inside);
                lock (_opened) most = Math.Max(most, now);
                // The first call stays inside until the test lets it go.
                if (System.Threading.Interlocked.Increment(ref calls) == 1)
                {
                    entered.Set();
                    leave.Wait();
                }
                System.Threading.Interlocked.Decrement(ref inside);
            }
            HidHideController.ControlOpenSeam = () => { Inside(); return 0; };
            HidHideController.IoSeam = (ioctl, inBuf, outBuf) => { Inside(); return _driver.Io(ioctl, inBuf, outBuf); };

            var holder = new System.Threading.Thread(() => CallControlDevice(first)) { IsBackground = true };
            var waiter = new System.Threading.Thread(() => CallControlDevice(second)) { IsBackground = true };
            int mostWhileHeld;
            try
            {
                holder.Start();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
                waiter.Start();

                // The second call ends up blocked at the gate. With no gate it
                // walks in beside the first and runs to its end. Either way
                // its thread stops running, and no clock decides which.
                var patience = System.Diagnostics.Stopwatch.StartNew();
                var spin = new System.Threading.SpinWait();
                while (waiter.IsAlive
                    && (waiter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
                {
                    Assert.True(patience.Elapsed < TimeSpan.FromSeconds(30), "the second call neither waited nor ended");
                    spin.SpinOnce();
                }
                lock (_opened) mostWhileHeld = most;
            }
            finally
            {
                leave.Set();
            }
            Assert.True(holder.Join(TimeSpan.FromSeconds(30)));
            Assert.True(waiter.Join(TimeSpan.FromSeconds(30)));

            Assert.Equal(1, mostWhileHeld);
            lock (_opened) Assert.Equal(1, most);
            Assert.True(calls >= 2);
        }

        /// <summary>The real calls, with nothing to change: this process's
        /// native image path, and the probe on a device that is not
        /// there.</summary>
        [Fact]
        public void TheNativePathAndTheProbeAnswerForReal()
        {
            HidHideController.OpenSeam = null;

            string native = HidHideController.CurrentProcessNativeImagePath();
            Assert.StartsWith(@"\Device\", native);
            Assert.EndsWith(@"\" + Path.GetFileName(Environment.ProcessPath), native, StringComparison.OrdinalIgnoreCase);

            Assert.False(HidHideController.TryOpenAsThisProcess(
                @"\\?\HID#VID_F00D&PID_0484#0&0&0&0#{4d1e55b2-f16f-11cf-88cb-001111000030}", out int error));
            Assert.NotEqual(0, error);
            Assert.NotEqual(ERROR_ACCESS_DENIED, error);
        }

        /// <summary>Every write that unhides a tablet releases its capture
        /// first. Standing down and taking a hide back both unhide, so each
        /// runs the release before its write, whatever the rows ask. No
        /// engine runs in these tests, so the order is read from the
        /// source.</summary>
        [Fact]
        public void StandingDownAndTakingBackReleaseTabletsFirst()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string svc = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Services", "InputService.cs"));
            int apply = svc.IndexOf("public void ApplyDeviceHiding()", StringComparison.Ordinal);
            Assert.True(apply > 0);

            int standDown = svc.IndexOf("bool standDown = inverse || _hidHideRefusedPadForge;", apply, StringComparison.Ordinal);
            int prepare = svc.IndexOf("PrepareTabletVisibilityChanges(snapshot, standDown);", apply, StringComparison.Ordinal);
            int write = svc.IndexOf("bool synced = HidHideController.SyncManagedDevices(desiredIds, out var added, out var removed, out var heldBack, allowAdditions: !whitelistTrouble);", apply, StringComparison.Ordinal);
            Assert.True(standDown > apply && prepare > standDown && write > prepare);

            int refused = svc.IndexOf("_hidHideRefusedPadForge = true;", write, StringComparison.Ordinal);
            int release = svc.IndexOf("PrepareTabletVisibilityChanges(snapshot, hideNone: true);", refused, StringComparison.Ordinal);
            int takeBack = svc.IndexOf("synced = HidHideController.SyncManagedDevices(desiredIds, out _, out var undone);", refused, StringComparison.Ordinal);
            Assert.True(refused > write && release > refused && takeBack > release);

            string tablets = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Services", "InputService.Tablets.cs"));
            Assert.Contains("if (_requestedTabletHides.Contains(device.InstanceGuid) && (hideNone || row?.HidHideEnabled != true))", tablets);
        }

        /// <summary>The probe opens the way SDL's enumeration does: no
        /// access rights, read and write shared, overlapped (hidapi
        /// windows/hid.c, open_device(path, FALSE)). An open that asks for
        /// read access is refused on a keyboard or mouse collection, which
        /// Windows keeps for its own drivers, and that refusal would read as
        /// HidHide's.</summary>
        [Fact]
        public void TheProbeOpensTheWaySdlEnumerates()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string ctl = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Common", "HidHideController.cs"));
            int at = ctl.IndexOf("internal static bool TryOpenAsThisProcess(", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = ctl.Substring(at, 900);
            Assert.Contains("CreateFileW(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,", body);
            Assert.Contains("IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);", body);
        }

        // ── A reconnected controller, a receiver, an engine start ──

        private const string RemoteUsb = @"USB\VID_F00D&PID_0484\REMOTE";
        private readonly List<string> _cycled = new();
        private long _releaseClock = 1_000_000;

        /// <summary>The release resolves the remote to a USB device of its
        /// own and records each cycle instead of making one.</summary>
        private void ReleaseCyclesTheRemote()
        {
            _svc.ControllerRelease.Resolve = (id, _, _) =>
                string.Equals(id, RemoteId, StringComparison.OrdinalIgnoreCase) ? (RemoteUsb, null) : (null, "test");
            _svc.ControllerRelease.CyclePort = usb => { lock (_cycled) _cycled.Add(usb); };
            _svc.ControllerRelease.Now = () => _releaseClock;
            _svc.ControllerRelease.Schedule = (_, _) => { };
        }

        private string[] Cycled()
        {
            _svc.ControllerRelease.Pending.Wait(TimeSpan.FromSeconds(10));
            lock (_cycled) return _cycled.ToArray();
        }

        /// <summary>A controller connected before HidHide was installed is
        /// opened through a device stack HidHide's filter never sat on, so the
        /// check after the hide proves nothing about HidHide's verdict on
        /// PadForge. The release rebuilds the stack, and the apply after the
        /// controller returns opens it there. Refused then, PadForge takes
        /// the hide back and says so.</summary>
        [Fact]
        public void AReleasedControllerIsCheckedAgainWhenItReturns()
        {
            ReleaseCyclesTheRemote();
            _svc.ApplyDeviceHiding();
            Assert.Equal(new[] { RemoteUsb }, Cycled());
            Assert.Single(_opened);

            _openAnswer = (false, ERROR_ACCESS_DENIED);
            _svc.ApplyDeviceHiding();

            Assert.Equal(2, _opened.Count);
            Assert.False(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);
        }

        /// <summary>The second check runs once. Allowed, the hide stays and
        /// the apply after it opens nothing.</summary>
        [Fact]
        public void AReturnedControllerIsCheckedOnce()
        {
            ReleaseCyclesTheRemote();
            _svc.ApplyDeviceHiding();
            Cycled();

            string mark = Mark();
            _svc.ApplyDeviceHiding();
            Assert.Equal(2, _opened.Count);
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.Single(LogLinesAfter(mark, "HIDHIDE reach ok after the release"));

            _svc.ApplyDeviceHiding();
            Assert.Equal(2, _opened.Count);
        }

        /// <summary>The same answer for a controller that comes back on a
        /// new device stack, the first one HidHide's filter sits on.
        /// Refused there, PadForge announces itself and opens the controller
        /// again. That open counts as the check of the returned controller,
        /// so the hide stays and the apply after it opens nothing.</summary>
        [Fact]
        public void AReturnedControllerRefusedOnItsNewStackIsOpenedAgainOnceAnnounced()
        {
            ReleaseCyclesTheRemote();
            _svc.ApplyDeviceHiding();
            Cycled();
            Assert.Single(_opened);

            // The announcement this apply makes on finding the driver comes
            // before the driver listens, and the one after the refusal
            // lands.
            ADriverThatLearnsPadForgeFromItsImage(listensFrom: 2);
            string mark = Mark();
            _svc.ApplyDeviceHiding();

            Assert.Equal(3, _opened.Count);
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.NotEqual(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);
            Assert.Single(LogLinesAfter(mark, "HIDHIDE reach ok once HidHide had PadForge's image name"));

            _svc.ApplyDeviceHiding();
            Assert.Equal(3, _opened.Count);
        }

        /// <summary>Until the controller is back its interface is gone, so
        /// nothing answers and the next apply asks again.</summary>
        [Fact]
        public void AControllerNotBackYetIsAskedAgain()
        {
            ReleaseCyclesTheRemote();
            _svc.ApplyDeviceHiding();
            Cycled();

            _interface = null;
            _svc.ApplyDeviceHiding();
            Assert.Single(_opened);
            Assert.True(Names(_driver.Blacklist, RemoteId));

            _interface = RemotePath;
            _svc.ApplyDeviceHiding();
            Assert.Equal(2, _opened.Count);
        }

        /// <summary>A lock the apply waits on dispatches the window messages
        /// other threads have sent to the UI thread, and the message for a
        /// device arrival asks for an apply from inside that wait. The fake
        /// driver stands in for the message: it asks while the first apply
        /// sits between its read of HidHide's list and its write. An apply
        /// that ran right there wrote the list itself and handed the remote
        /// to the release, the first apply then wrote the same list and
        /// handed it over again, and the release held a second cycle for the
        /// cooldown's end. The apply asked for inside now runs once the
        /// first is done and finds nothing to add: one write, one cycle,
        /// nothing held, two applies.</summary>
        [Fact]
        public void AnApplyAskedForDuringAnApplyRunsAfterIt()
        {
            ReleaseCyclesTheRemote();
            var held = new List<long>();
            _svc.ControllerRelease.Schedule = (ms, _) => { lock (held) held.Add(ms); };
            int asked = 0, applies = 0;
            HidHideController.IoSeam = (ioctl, inBuf, outBuf) =>
            {
                if (ioctl == IOCTL_GET_WLINVERSE) applies++;
                if (ioctl == IOCTL_SET_BLACKLIST && asked++ == 0) _svc.ApplyAutomaticDeviceHiding();
                return _driver.Io(ioctl, inBuf, outBuf);
            };

            _svc.ApplyDeviceHiding();

            Assert.Equal(new[] { RemoteUsb }, Cycled());
            lock (held) Assert.Empty(held);
            Assert.Single(_driver.BlacklistWrites);
            Assert.Equal(1, asked);
            Assert.Equal(2, applies);
        }

        /// <summary>An engine start clears the list a session before left
        /// and then applies, and the clear waits on the same gate as any
        /// call to the control device. A device arrival dispatched inside
        /// the clear once ran a whole apply there: it hid the remote and
        /// handed it to the release, the clear then emptied the list, and
        /// the start's own apply hid the remote and handed it over a second
        /// time, for a second cycle held until the cooldown's end. The clear
        /// and the apply are now one apply. The request is owed and runs
        /// after both, where it finds nothing to add. The release reads
        /// HidHide's list only once the start is done, so no batch decides
        /// in the moment the list is empty.</summary>
        [Fact]
        public void ARequestDuringAnEngineStartsClearRunsAfterTheStartsApply()
        {
            ReleaseCyclesTheRemote();
            var held = new List<long>();
            _svc.ControllerRelease.Schedule = (ms, _) => { lock (held) held.Add(ms); };
            using var started = new System.Threading.ManualResetEventSlim(false);
            var read = _svc.ControllerRelease.ReadBlacklist;
            _svc.ControllerRelease.ReadBlacklist = () =>
            {
                started.Wait(TimeSpan.FromSeconds(30));
                return read();
            };
            int asked = 0, applies = 0, appliesWhenAsked = -1;
            HidHideController.IoSeam = (ioctl, inBuf, outBuf) =>
            {
                if (ioctl == IOCTL_GET_WLINVERSE) applies++;
                // The first write is the clear.
                if (ioctl == IOCTL_SET_BLACKLIST && asked++ == 0)
                {
                    _svc.ApplyAutomaticDeviceHiding();
                    appliesWhenAsked = applies;
                }
                return _driver.Io(ioctl, inBuf, outBuf);
            };

            _svc.ApplyDeviceHidingAtEngineStart();
            started.Set();

            Assert.Equal(0, appliesWhenAsked);
            Assert.Equal(2, applies);
            Assert.Equal(new[] { RemoteUsb }, Cycled());
            lock (held) Assert.Empty(held);
            Assert.Equal(2, _driver.BlacklistWrites.Count);
            Assert.Empty(_driver.BlacklistWrites[0]);
            Assert.True(Names(_driver.BlacklistWrites[1], RemoteId));
        }

        private const System.Reflection.BindingFlags PrivateField =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        private T ServiceField<T>(string name)
            => (T)typeof(InputService).GetField(name, PrivateField).GetValue(_svc);

        private void SetServiceField(string name, object value)
            => typeof(InputService).GetField(name, PrivateField).SetValue(_svc, value);

        /// <summary>The apply owed to a request that arrived during another
        /// goes through the checks every automatic apply passes. A service
        /// disposed meanwhile gets no apply, and neither does an engine that
        /// is stopping and still holds its manager. Both ways in are held
        /// to it, a plain apply and an engine start's, with the request
        /// arriving at the write that hides the remote.</summary>
        [Theory]
        [InlineData(false, "")]
        [InlineData(true, "")]
        [InlineData(false, "disposed")]
        [InlineData(true, "disposed")]
        [InlineData(false, "stopping")]
        [InlineData(true, "stopping")]
        public void AnOwedApplyPassesTheLifetimeChecks(bool atEngineStart, string retired)
        {
            int asked = 0, applies = 0;
            HidHideController.IoSeam = (ioctl, inBuf, outBuf) =>
            {
                if (ioctl == IOCTL_GET_WLINVERSE) applies++;
                // A write that names an entry, so not an engine start's clear.
                if (ioctl == IOCTL_SET_BLACKLIST && inBuf.Length > 2 && asked++ == 0)
                {
                    _svc.ApplyAutomaticDeviceHiding();
                    if (retired == "disposed") SetServiceField("_disposed", 1);
                    if (retired == "stopping")
                    {
                        // An identity marker, as the lifecycle tests make one:
                        // it owns no readers or native resources to finalize.
                        var manager = (InputManager)System.Runtime.CompilerServices.RuntimeHelpers
                            .GetUninitializedObject(typeof(InputManager));
                        GC.SuppressFinalize(manager);
                        SetServiceField("_stopped", 1);
                        SetServiceField("_inputManager", manager);
                    }
                }
                return _driver.Io(ioctl, inBuf, outBuf);
            };
            try
            {
                if (atEngineStart) _svc.ApplyDeviceHidingAtEngineStart();
                else _svc.ApplyDeviceHiding();

                Assert.Equal(1, asked);
                Assert.Equal(retired.Length == 0 ? 2 : 1, applies);
                Assert.True(Names(_driver.Blacklist, RemoteId));
            }
            finally
            {
                SetServiceField("_disposed", 0);
                SetServiceField("_stopped", 0);
                SetServiceField("_inputManager", null);
            }
        }

        /// <summary>An apply that throws leaves neither flag set, whichever
        /// way in it took, although a request arrived before it failed. The
        /// next request is then an apply of its own. A flag left set would
        /// turn every later apply into a note that one is owed.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnApplyThatThrowsLeavesNoFlagSet(bool atEngineStart)
        {
            bool thrown = false;
            HidHideController.IoSeam = (ioctl, inBuf, outBuf) =>
            {
                if (ioctl == IOCTL_GET_WLINVERSE && !thrown)
                {
                    thrown = true;
                    _svc.ApplyAutomaticDeviceHiding();
                    throw new InvalidOperationException("the driver call failed");
                }
                return _driver.Io(ioctl, inBuf, outBuf);
            };

            Assert.Throws<InvalidOperationException>(() =>
            {
                if (atEngineStart) _svc.ApplyDeviceHidingAtEngineStart();
                else _svc.ApplyDeviceHiding();
            });
            Assert.False(ServiceField<bool>("_deviceHidingApplying"));
            Assert.False(ServiceField<bool>("_deviceHidingApplyOwed"));
            Assert.False(Names(_driver.Blacklist, RemoteId));

            _svc.ApplyDeviceHiding();
            Assert.True(Names(_driver.Blacklist, RemoteId));
        }

        /// <summary>For a moment after its port is cycled, a controller is
        /// still on the device stack it had, and an apply that ran then would
        /// open that stack and settle the check on it. The second check waits
        /// until Windows dates the controller's arrival anew, and a refusal
        /// on the new stack is then acted on.</summary>
        [Fact]
        public void AControllerStillOnItsOldStackIsNotCheckedYet()
        {
            ReleaseCyclesTheRemote();
            _arrival = DateTime.UtcNow.AddMinutes(-10);
            _svc.ApplyDeviceHiding();
            Cycled();

            _openAnswer = (false, ERROR_ACCESS_DENIED);
            _svc.ApplyDeviceHiding();
            Assert.Single(_opened);
            Assert.True(Names(_driver.Blacklist, RemoteId));

            _arrival = DateTime.UtcNow.AddMinutes(10);
            _svc.ApplyDeviceHiding();
            Assert.Equal(2, _opened.Count);
            Assert.False(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);
        }

        private static readonly DateTime ConnectedAt = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>A hidden controller that leaves and comes back by itself,
        /// a replug, is on a device stack built since the last check, just as
        /// one the release reconnected. The release never cycles this one, so
        /// nothing names it for a second check. The apply sees that Windows
        /// dates its arrival anew and opens it once more. Refused then,
        /// PadForge takes the hide back and says so.</summary>
        [Fact]
        public void AHiddenControllerThatReconnectsByItselfIsCheckedAgain()
        {
            _arrival = ConnectedAt;
            _svc.ApplyDeviceHiding();
            Assert.Single(_opened);
            Assert.Empty(Cycled());

            _arrival = ConnectedAt.AddMinutes(30);
            _openAnswer = (false, ERROR_ACCESS_DENIED);
            _svc.ApplyDeviceHiding();

            Assert.Equal(2, _opened.Count);
            Assert.False(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);
        }

        /// <summary>The check after a reconnect runs once. Allowed, the hide
        /// stays, the log says which check passed, and the apply after it
        /// opens nothing.</summary>
        [Fact]
        public void AReconnectedControllerIsCheckedOnce()
        {
            _arrival = ConnectedAt;
            _svc.ApplyDeviceHiding();

            _arrival = ConnectedAt.AddMinutes(30);
            string mark = Mark();
            _svc.ApplyDeviceHiding();
            Assert.Equal(2, _opened.Count);
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.Single(LogLinesAfter(mark, "HIDHIDE reach ok after a reconnect"));
            Assert.Empty(LogLinesAfter(mark, "HIDHIDE reach ok after the release"));

            _svc.ApplyDeviceHiding();
            Assert.Equal(2, _opened.Count);
        }

        /// <summary>A controller that is back while its interface does not
        /// answer yet settles nothing, so the next apply asks again, and the
        /// one after the answer does not.</summary>
        [Fact]
        public void AReconnectedControllerThatDoesNotAnswerYetIsAskedAgain()
        {
            _arrival = ConnectedAt;
            _svc.ApplyDeviceHiding();

            _arrival = ConnectedAt.AddMinutes(30);
            _interface = null;
            _svc.ApplyDeviceHiding();
            Assert.Single(_opened);
            Assert.True(Names(_driver.Blacklist, RemoteId));

            _interface = RemotePath;
            _svc.ApplyDeviceHiding();
            Assert.Equal(2, _opened.Count);

            _svc.ApplyDeviceHiding();
            Assert.Equal(2, _opened.Count);
        }

        /// <summary>A controller set to hide while it is away is on the list
        /// before it has any device stack. When it connects, PadForge opens
        /// it itself, although the check passed long before on another
        /// controller and this apply adds nothing.</summary>
        [Fact]
        public void AControllerHiddenWhileAwayIsCheckedWhenItArrives()
        {
            const string awayPath = @"\\?\HID#VID_F00D&PID_0499#9&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            string awayId = HidHideController.DevicePathToInstanceId(awayPath);
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "Pad that is away",
                VendorId = 0xF00D,
                ProdId = 0x0499,
                DevicePath = awayPath,
                HidHideEnabled = true,
            });
            DateTime? awayArrival = null;
            string awayInterface = null;
            HidHideController.ArrivalSeam = id =>
                string.Equals(id, awayId, StringComparison.OrdinalIgnoreCase) ? awayArrival : _arrival;
            HidHideController.InterfaceSeam = id =>
                string.Equals(id, awayId, StringComparison.OrdinalIgnoreCase) ? awayInterface
                : string.Equals(id, RemoteId, StringComparison.OrdinalIgnoreCase) ? _interface : null;
            _arrival = ConnectedAt;

            _svc.ApplyDeviceHiding();
            Assert.True(Names(_driver.Blacklist, awayId));
            Assert.Equal(new[] { RemotePath }, _opened);

            awayArrival = ConnectedAt.AddHours(1);
            awayInterface = awayPath;
            int writes = _driver.BlacklistWrites.Count;
            _svc.ApplyDeviceHiding();

            Assert.Equal(writes, _driver.BlacklistWrites.Count);
            Assert.Equal(new[] { RemotePath, awayPath }, _opened);
        }

        /// <summary>The apply tells a return by an arrival date that differs
        /// from the one it last saw, not by one later than the cycle. A clock
        /// set back an hour right after a port cycle dates the controller's
        /// return before the cycle, and the controller is still opened on the
        /// apply after it is back, where it once waited out the hour.</summary>
        [Fact]
        public void AClockSetBackDoesNotHoldTheCheckAfterAPortCycle()
        {
            ReleaseCyclesTheRemote();
            // The cycle is stamped by a clock an hour ahead of the one every
            // later reading comes from, which is a clock set back an hour
            // right after the stamp.
            var cycled = DateTime.UtcNow.AddHours(1);
            _svc.ControllerRelease.UtcNow = () => cycled;
            // Connected five minutes before the cycle, by the clock that
            // stamped it.
            _arrival = cycled.AddMinutes(-5);
            _svc.ApplyDeviceHiding();
            Assert.Equal(new[] { RemoteUsb }, Cycled());
            Assert.Single(_opened);

            // Back two seconds after the cycle, dated by the clock as it now
            // reads: before the cycle, and before the arrival it replaces.
            _arrival = cycled.AddSeconds(2).AddHours(-1);
            Assert.False(InputService.ReturnedSinceCycle(cycled, _arrival, DateTime.UtcNow));
            _openAnswer = (false, ERROR_ACCESS_DENIED);
            _svc.ApplyDeviceHiding();

            Assert.Equal(2, _opened.Count);
            Assert.False(Names(_driver.Blacklist, RemoteId));
        }

        /// <summary>The arrival date is kept only while the controller is
        /// hidden. Shown, replugged and hidden again, it is checked as any
        /// new hide is, and the log does not call that a reconnect.</summary>
        [Fact]
        public void AControllerShownAndHiddenAgainIsCheckedAsANewHide()
        {
            var remote = SettingsManager.UserDevices.Items[0];
            _arrival = ConnectedAt;
            _svc.ApplyDeviceHiding();

            remote.HidHideEnabled = false;
            _svc.ApplyDeviceHiding();
            Assert.False(Names(_driver.Blacklist, RemoteId));

            _arrival = ConnectedAt.AddMinutes(30);
            remote.HidHideEnabled = true;
            string mark = Mark();
            _svc.ApplyDeviceHiding();

            Assert.Equal(2, _opened.Count);
            Assert.Single(LogLinesAfter(mark, "HIDHIDE reach ok: PadForge opened"));
            Assert.Empty(LogLinesAfter(mark, "HIDHIDE reach ok after"));
        }

        /// <summary>An apply that finds a hidden controller back on a new
        /// stack opens it there, and that open answers for the process. A
        /// controller hidden in the same apply is not opened on top of
        /// it.</summary>
        [Fact]
        public void AReconnectAndANewHideInOneApplyOpenOnce()
        {
            const string otherPath = @"\\?\HID#VID_F00D&PID_0499#9&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            string otherId = HidHideController.DevicePathToInstanceId(otherPath);
            var other = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "Second pad",
                VendorId = 0xF00D,
                ProdId = 0x0499,
                DevicePath = otherPath,
                IsOnline = true,
            };
            SettingsManager.UserDevices.Items.Add(other);
            HidHideController.InterfaceSeam = id =>
                string.Equals(id, otherId, StringComparison.OrdinalIgnoreCase) ? otherPath
                : string.Equals(id, RemoteId, StringComparison.OrdinalIgnoreCase) ? _interface : null;
            _arrival = ConnectedAt;
            _svc.ApplyDeviceHiding();
            Assert.Single(_opened);

            other.HidHideEnabled = true;
            _arrival = ConnectedAt.AddMinutes(30);
            _svc.ApplyDeviceHiding();

            Assert.True(Names(_driver.Blacklist, otherId));
            Assert.Equal(new[] { RemotePath, RemotePath }, _opened);
        }

        /// <summary>The release reads HidHide's list through the control
        /// device, which takes one client at a time. While another program
        /// holds it the list does not read and the open answers access
        /// denied: the driver is there, so the batch waits and reads again. A
        /// control device that is not there at all means nothing is hidden,
        /// and the batch ends.</summary>
        [Theory]
        [InlineData(5, true)]
        [InlineData(2, false)]
        public void TheReleaseTellsABusyDriverFromOneThatIsGone(int openError, bool waits)
        {
            ReleaseCyclesTheRemote();
            var scheduled = new List<Action>();
            _svc.ControllerRelease.Schedule = (_, action) => { lock (scheduled) scheduled.Add(action); };
            // The batch is held back until the list has stopped reading.
            bool listing = true;
            _svc.ControllerRelease.RowsPending = () => listing;

            _svc.ApplyDeviceHiding();
            Assert.Empty(Cycled());
            lock (scheduled) Assert.Single(scheduled);

            _driver.RefuseBlacklistReads = true;
            HidHideController.ControlOpenSeam = () => openError;
            listing = false;
            scheduled[0]();
            Assert.Empty(Cycled());
            lock (scheduled) Assert.Equal(waits ? 2 : 1, scheduled.Count);

            _driver.RefuseBlacklistReads = false;
            HidHideController.ControlOpenSeam = null;
            if (waits) scheduled[1]();
            Assert.Equal(waits ? new[] { RemoteUsb } : Array.Empty<string>(), Cycled());
        }

        /// <summary>A program that holds HidHide's control device for longer
        /// than the release waits ends the wait: the remote's ids are
        /// dropped with one line, no timer is left to ask again, and no
        /// cycle follows when that program lets go, however much later. The
        /// hide the apply wrote is still on HidHide's list, and the next
        /// apply hands nothing over for it.</summary>
        [Fact]
        public void AReleaseThatWaitedOutTheListLeavesTheHide()
        {
            ReleaseCyclesTheRemote();
            var scheduled = new List<Action>();
            _svc.ControllerRelease.Schedule = (_, action) => { lock (scheduled) scheduled.Add(action); };
            _svc.ControllerRelease.Log = line => { lock (_releaseLines) _releaseLines.Add(line); };
            // The batch is held back until the list has stopped reading.
            bool listing = true;
            _svc.ControllerRelease.RowsPending = () => listing;
            _svc.ApplyDeviceHiding();
            Cycled();

            _driver.RefuseBlacklistReads = true;
            HidHideController.ControlOpenSeam = () => ERROR_ACCESS_DENIED;
            listing = false;
            scheduled[0]();
            Cycled();
            _releaseClock += HiddenControllerRelease.ListWaitMs;
            scheduled[1]();
            Assert.Empty(Cycled());
            lock (scheduled) Assert.Equal(2, scheduled.Count);
            lock (_releaseLines)
            {
                string dropped = Assert.Single(_releaseLines, l => l.StartsWith("HIDHIDE release dropped"));
                Assert.Contains(RemoteId, dropped, StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith("]: HidHide's blacklist did not read for 60 s, so no USB port is cycled for these ids. Their hide is left as it is", dropped);
            }

            // The other program lets go.
            _driver.RefuseBlacklistReads = false;
            HidHideController.ControlOpenSeam = null;
            _svc.ApplyDeviceHiding();
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.Empty(Cycled());
        }

        // A wired pad's walk up to its hub, as the release reads it.
        private static readonly Guid ReleaseHidClass = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
        private static readonly Guid ReleaseUsbClass = new("36fc9e60-c465-11cf-8056-444553540000");
        private static readonly Guid ReleasePadContainer = new("11111111-2222-3333-4444-555555555555");
        private const string WiredPadId = @"HID\VID_F00D&PID_0CE6&MI_03\7&1A2B&0&0000";
        private const string WiredPadUsb = @"USB\VID_F00D&PID_0CE6\SERIAL01";

        private static List<HidHideController.ReleaseHop> AWiredPadsWalk() => new()
        {
            new(WiredPadId, ReleaseHidClass, ReleasePadContainer, null),
            new(@"USB\VID_F00D&PID_0CE6&MI_03\6&3C4D&0&0003", ReleaseHidClass, ReleasePadContainer, "HidUsb"),
            new(WiredPadUsb, ReleaseUsbClass, ReleasePadContainer, "usbccgp"),
            new(@"USB\ROOT_HUB30\4&5E6F&0&0", ReleaseUsbClass, Guid.Empty, "USBHUB3"),
        };

        /// <summary>The walk for a hidden id counts the grace for a
        /// controller that just connected up to the time its id was handed
        /// to the release, whatever the clock reads when it is asked. A pad
        /// that arrived two seconds before the hand-over is left alone
        /// however much later the walk runs. One that arrived after the
        /// hand-over came back under its hide and is left alone as well. A
        /// date that does not read holds nothing back.</summary>
        [Fact]
        public void TheWalkCountsTheArrivalGraceUpToTheHandOver()
        {
            DateTime arrived = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime? arrival = arrived;
            HidHideController.ReleaseWalkSeam = _ => (AWiredPadsWalk(), 0, arrival);

            Assert.Null(HidHideController.FindUsbDeviceToCycle(WiredPadId, null, arrived.AddSeconds(2), out string reason));
            Assert.Equal("connected moments ago", reason);
            Assert.Equal(WiredPadUsb, HidHideController.FindUsbDeviceToCycle(WiredPadId, null,
                arrived + HidHideController.ReleaseArrivalGrace, out _));
            Assert.Null(HidHideController.FindUsbDeviceToCycle(WiredPadId, null, arrived.AddSeconds(-30), out reason));
            Assert.Equal("connected moments ago", reason);

            arrival = null;
            Assert.Equal(WiredPadUsb, HidHideController.FindUsbDeviceToCycle(WiredPadId, null, arrived.AddSeconds(2), out _));

            HidHideController.ReleaseWalkSeam = _ => null;
            Assert.Null(HidHideController.FindUsbDeviceToCycle(WiredPadId, null, arrived, out reason));
            Assert.Equal("not present", reason);
        }

        /// <summary>The release hands that walk the time of the hand-over:
        /// a pad hidden two seconds after it arrived is left connected by a
        /// batch that decides ten seconds later, once the engine has listed
        /// its devices.</summary>
        [Fact]
        public void TheReleaseGivesTheWalkTheHandOverTime()
        {
            DateTime handedOver = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
            HidHideController.ReleaseWalkSeam = _ => (AWiredPadsWalk(), 0, handedOver.AddSeconds(-2));
            DateTime wall = handedOver;
            long ticks = 1_000_000;
            bool listing = true;
            var lines = new List<string>();
            var cycled = new List<string>();
            var scheduled = new List<Action>();
            using var release = new HiddenControllerRelease
            {
                CyclePort = usb => { lock (cycled) cycled.Add(usb); },
                Now = () => ticks,
                UtcNow = () => wall,
                Log = line => { lock (lines) lines.Add(line); },
                Schedule = (_, action) => { lock (scheduled) scheduled.Add(action); },
                ReadBlacklist = () => (new[] { WiredPadId }, true),
                RowsPending = () => listing,
            };

            release.Release(new[] { WiredPadId });
            Assert.True(BatchesDone(release));
            lock (scheduled) Assert.Single(scheduled);

            wall = handedOver.AddSeconds(8);
            ticks += 8_000;
            listing = false;
            scheduled[0]();
            Assert.True(BatchesDone(release));

            lock (cycled) Assert.Empty(cycled);
            lock (lines) Assert.Single(lines, l => l == $"HIDHIDE release skipped {WiredPadId}: connected moments ago");
        }

        /// <summary>Waits for every batch the release has queued so far.
        /// False when one is still running after the wait.</summary>
        private static bool BatchesDone(HiddenControllerRelease release)
            => release.Pending.Wait(TimeSpan.FromSeconds(30));

        /// <summary>The return is read from the arrival date: later than the
        /// cycle means the controller is back on a new stack. A date that
        /// does not read, or one from before the cycle, stops holding the
        /// check once the wait has run out. The wait runs on the wall clock
        /// the two times were read from, so with a clock set back between the
        /// cycle and the return this test says no for the set-back plus the
        /// wait. The apply has a second way to see the return, which
        /// AClockSetBackDoesNotHoldTheCheckAfterAPortCycle covers.</summary>
        [Fact]
        public void AReturnIsReadFromTheArrivalDate()
        {
            var cycled = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
            var wait = InputService.ReprobeWithoutArrival;

            Assert.False(InputService.ReturnedSinceCycle(cycled, cycled.AddSeconds(-20), cycled.AddSeconds(1)));
            Assert.False(InputService.ReturnedSinceCycle(cycled, cycled, cycled.AddSeconds(1)));
            Assert.True(InputService.ReturnedSinceCycle(cycled, cycled.AddSeconds(2), cycled.AddSeconds(3)));
            Assert.False(InputService.ReturnedSinceCycle(cycled, null, cycled.AddSeconds(5)));
            Assert.False(InputService.ReturnedSinceCycle(cycled, null, cycled + wait));
            Assert.True(InputService.ReturnedSinceCycle(cycled, null, cycled + wait + TimeSpan.FromSeconds(1)));
            Assert.True(InputService.ReturnedSinceCycle(cycled, cycled.AddSeconds(-20), cycled + wait + TimeSpan.FromSeconds(1)));

            // The clock is set back an hour right after the cycle is stamped.
            // The controller returns two seconds later, and Windows dates
            // that arrival 59 minutes and 58 seconds before the stamp. The
            // answer stays no until the clock reads the wait past the stamp,
            // which is an hour and the wait after the cycle.
            var back = TimeSpan.FromHours(1);
            var arrival = cycled.AddSeconds(2) - back;
            DateTime ClockAt(TimeSpan sinceCycle) => cycled + sinceCycle - back;
            Assert.False(InputService.ReturnedSinceCycle(cycled, arrival, ClockAt(TimeSpan.FromSeconds(60))));
            Assert.False(InputService.ReturnedSinceCycle(cycled, arrival, ClockAt(back + wait)));
            Assert.True(InputService.ReturnedSinceCycle(cycled, arrival, ClockAt(back + wait + TimeSpan.FromSeconds(1))));
        }

        /// <summary>With Keep Devices Cloaked Between Launches off, engine
        /// start clears the driver's list and hides everything again, so each
        /// start hands every hidden USB controller to the release, which
        /// takes it from whatever opened it while PadForge was closed. That
        /// is what the release is for (fd186c7e). The clear and the apply are
        /// the engine start's one apply. That Start makes that one call and
        /// clears nothing by itself is read from the source, since no engine
        /// runs here.</summary>
        [Fact]
        public void AnEngineStartReconnectsAHiddenUsbController()
        {
            ReleaseCyclesTheRemote();
            _svc.ApplyDeviceHiding();
            Assert.Single(Cycled());

            // Engine start, the setting off: the list is cleared and the apply
            // hides everything again, more than a cooldown later.
            _releaseClock += HiddenControllerRelease.CooldownMs;
            int writes = _driver.BlacklistWrites.Count;
            _svc.ApplyDeviceHidingAtEngineStart();

            Assert.Empty(_driver.BlacklistWrites[writes]);
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(new[] { RemoteUsb, RemoteUsb }, Cycled());

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string svc = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Services", "InputService.cs"));
            int start = svc.IndexOf("public void Start()", StringComparison.Ordinal);
            Assert.True(start > 0);
            int end = svc.IndexOf("private int _stopped;", start, StringComparison.Ordinal);
            Assert.True(end > start);
            string body = svc.Substring(start, end - start);
            Assert.Contains("            ApplyDeviceHidingAtEngineStart();", body);
            Assert.DoesNotContain("HidHideController.ClearAll();", body);
            Assert.DoesNotContain("            ApplyDeviceHiding();", body);
        }

        private const string PadOnePath = @"\\?\HID#VID_F00D&PID_1142&MI_01#8&1a2b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string PadTwoPath = @"\\?\HID#VID_F00D&PID_1142&MI_02#8&1a2b3c&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string Dongle = @"USB\VID_F00D&PID_1142\DONGLE";

        /// <summary>Two rows on one receiver, the first hidden, and the release
        /// resolving through the real device decision with the chain a
        /// Steam Controller dongle has. Returns the first pad's id.</summary>
        private string TwoRowsOnOneReceiver(int secondRowType)
        {
            string padOne = HidHideController.DevicePathToInstanceId(PadOnePath);
            string padTwo = HidHideController.DevicePathToInstanceId(PadTwoPath);
            HidHideController.ChainSeam = id =>
                string.Equals(id, padOne, StringComparison.OrdinalIgnoreCase)
                    ? new List<string> { padOne, @"USB\VID_F00D&PID_1142&MI_01\7&1&0&0001", Dongle }
                : string.Equals(id, padTwo, StringComparison.OrdinalIgnoreCase)
                    ? new List<string> { padTwo, @"USB\VID_F00D&PID_1142&MI_02\7&1&0&0002", Dongle }
                : new List<string> { id };
            HidHideController.InterfaceSeam = id =>
                string.Equals(id, padOne, StringComparison.OrdinalIgnoreCase) ? PadOnePath : null;

            SettingsManager.UserDevices.Items[0].HidHideEnabled = false;
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "Pad One",
                VendorId = 0xF00D,
                ProdId = 0x1142,
                DevicePath = PadOnePath,
                CapType = InputDeviceType.Gamepad,
                HidHideEnabled = true,
                IsOnline = true,
            });
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "Pad Two",
                VendorId = 0xF00D,
                ProdId = 0x1142,
                DevicePath = PadTwoPath,
                CapType = secondRowType,
                IsOnline = true,
            });

            var chain = new List<HidHideController.ReleaseHop>
            {
                new(padOne, Guid.Empty, Guid.Empty, null),
                new(@"USB\VID_F00D&PID_1142&MI_01\7&1&0&0001", Guid.Empty, Guid.Empty, "HidUsb"),
                new(Dongle, Guid.Empty, Guid.Empty, "usbccgp"),
                new(@"USB\ROOT_HUB30\4&5&0&0", Guid.Empty, Guid.Empty, "USBHUB3"),
            };
            _svc.ControllerRelease.Resolve = (id, rowsOn, _) =>
            {
                if (!string.Equals(id, padOne, StringComparison.OrdinalIgnoreCase)) return (null, "test");
                string usb = HidHideController.PickUsbDeviceToCycle(chain, 0, rowsOn, TimeSpan.FromMinutes(5), out string reason);
                return (usb, reason);
            };
            _svc.ControllerRelease.CyclePort = usb => { lock (_cycled) _cycled.Add(usb); };
            _svc.ControllerRelease.Log = line => { lock (_releaseLines) _releaseLines.Add(line); };
            return padOne;
        }

        private readonly List<string> _releaseLines = new();

        /// <summary>A Steam Controller dongle carries its pads as HID
        /// interfaces of one USB device and no XUSB node, so the device tree
        /// alone reads it as one controller. With a second pad on it as a row
        /// of its own, the dongle stays connected, and the visible pad keeps
        /// its link.</summary>
        [Fact]
        public void AReceiverCarryingAnotherControllerIsNotCycled()
        {
            string padOne = TwoRowsOnOneReceiver(InputDeviceType.Gamepad);

            _svc.ApplyDeviceHiding();

            Assert.True(Names(_driver.Blacklist, padOne));
            Assert.Empty(Cycled());
            lock (_releaseLines)
                Assert.Single(_releaseLines, l => l == $"HIDHIDE release skipped {padOne}: the USB device carries more than one controller");
        }

        /// <summary>Rows that are not controllers do not make a receiver: a
        /// touchpad row on the same USB device leaves one controller there,
        /// and the device is cycled.</summary>
        [Fact]
        public void ARowThatIsNotAControllerDoesNotHoldTheCycleBack()
        {
            TwoRowsOnOneReceiver(InputDeviceType.Touchpad);

            _svc.ApplyDeviceHiding();

            Assert.Equal(new[] { Dongle }, Cycled());
        }

        private const string PadThreePath = @"\\?\HID#VID_F00D&PID_1142&MI_03#8&1a2b3c&0&0002#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        private const string PadFourPath = @"\\?\HID#VID_F00D&PID_1142&MI_04#8&1a2b3c&0&0003#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string PadFivePath = @"\\?\HID#VID_F00D&PID_1142&MI_05#8&1a2b3c&0&0004#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        /// <summary>The rows per USB device count the controllers on each HID
        /// node, on every node of its chain. The rows the engine has online
        /// on a node are the controllers there: four on a GameCube adapter,
        /// whose pads SDL reads from one HID node, and one where a second pad
        /// of the model is remembered on the same port. A node with no row
        /// online is counted from the rows that remember it, while Windows
        /// lists the node as present, so the count does not wait for the poll
        /// thread to mark rows online. Rows with one serial number, or none,
        /// are one device's controllers and count each. Rows with different
        /// serial numbers are different devices that sat there in turn, and
        /// count as the one that is there now. A row whose node is gone, a
        /// synthetic path and a row that is not a controller are left
        /// out.</summary>
        [Fact]
        public void ControllerRowsAreCountedPerDeviceNode()
        {
            string padTwo = HidHideController.DevicePathToInstanceId(PadTwoPath);
            string padFour = HidHideController.DevicePathToInstanceId(PadFourPath);
            string padFive = HidHideController.DevicePathToInstanceId(PadFivePath);
            HidHideController.PresenceSeam = id =>
                string.Equals(id, padTwo, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, padFour, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, padFive, StringComparison.OrdinalIgnoreCase);
            var rows = new[]
            {
                new UserDevice { DevicePath = PadOnePath, CapType = InputDeviceType.Gamepad, IsOnline = true },
                new UserDevice { DevicePath = PadOnePath, CapType = InputDeviceType.Gamepad, IsOnline = true },
                new UserDevice { DevicePath = PadTwoPath, CapType = InputDeviceType.Joystick, IsOnline = false },
                new UserDevice { DevicePath = PadTwoPath, CapType = InputDeviceType.Gamepad, IsOnline = false },
                new UserDevice { DevicePath = PadThreePath, CapType = InputDeviceType.Gamepad, IsOnline = false },
                new UserDevice { DevicePath = PadFourPath, CapType = InputDeviceType.Gamepad, IsOnline = true },
                new UserDevice { DevicePath = PadFourPath.ToUpperInvariant(), CapType = InputDeviceType.Gamepad, IsOnline = false },
                new UserDevice { DevicePath = PadFivePath, CapType = InputDeviceType.Gamepad, IsOnline = false, SerialNumber = "0022aaccdee1" },
                new UserDevice { DevicePath = PadFivePath, CapType = InputDeviceType.Gamepad, IsOnline = false, SerialNumber = "0022AACCDEE2" },
                new UserDevice { DevicePath = PadFivePath, CapType = InputDeviceType.Gamepad, IsOnline = false, SerialNumber = "0022aaccdee2" },
                new UserDevice { DevicePath = PadFivePath, CapType = InputDeviceType.Gamepad, IsOnline = false },
                new UserDevice { DevicePath = PadTwoPath, CapType = InputDeviceType.Mouse, IsOnline = true },
                new UserDevice { DevicePath = "XInput#0", CapType = InputDeviceType.Gamepad, IsOnline = true },
            };
            var counts = InputService.CountControllerRowsPerUsbDevice(rows,
                id => new List<string> { id, Dongle, Dongle });

            Assert.Equal(2, counts[HidHideController.DevicePathToInstanceId(PadOnePath)]);
            Assert.Equal(2, counts[padTwo]);
            Assert.Equal(1, counts[padFour]);
            Assert.Equal(2, counts[padFive]);
            Assert.Equal(7, counts[Dongle]);
            Assert.False(counts.ContainsKey(HidHideController.DevicePathToInstanceId(PadThreePath)));
            Assert.False(counts.ContainsKey("XInput#0"));
        }

        /// <summary>The same two pads while the engine is stopped, when no
        /// row is online to say which of them is plugged in. Their rows carry
        /// different serial numbers, so they are two devices remembered on
        /// one port and one controller there now, and the port is
        /// cycled.</summary>
        [Fact]
        public void APadWithARememberedTwinIsStillCycledWhileTheEngineIsStopped()
        {
            string padOne = TwoRowsOnOneReceiver(InputDeviceType.Touchpad);
            SettingsManager.UserDevices.Items[1].SerialNumber = "0022aaccdee8";
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "Pad One's twin",
                VendorId = 0xF00D,
                ProdId = 0x1142,
                DevicePath = PadOnePath,
                SerialNumber = "0022aaccdee9",
                CapType = InputDeviceType.Gamepad,
                HidHideEnabled = true,
            });
            NoRowIsOnlineYet(padOne, HidHideController.DevicePathToInstanceId(PadTwoPath));

            _svc.ApplyDeviceHiding();

            Assert.True(Names(_driver.Blacklist, padOne));
            Assert.Equal(new[] { Dongle }, Cycled());
        }

        /// <summary>Two pads of one model plugged into the same port in turn
        /// leave two rows that name one path. The one the engine has online
        /// is the controller there, and the other is a memory, so the pad is
        /// one controller on its USB device and its port is cycled. Counting
        /// every row whose node is present read it as a receiver with two
        /// pads and never reconnected it.</summary>
        [Fact]
        public void APadWithARememberedTwinOnItsPortIsStillCycled()
        {
            string padOne = TwoRowsOnOneReceiver(InputDeviceType.Touchpad);
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "Pad One's twin",
                VendorId = 0xF00D,
                ProdId = 0x1142,
                DevicePath = PadOnePath,
                SerialNumber = "0022aaccdee9",
                CapType = InputDeviceType.Gamepad,
                HidHideEnabled = true,
                IsOnline = false,
            });
            HidHideController.PresenceSeam = _ => true;
            _svc.RowsCurrentSeam = () => true;

            _svc.ApplyDeviceHiding();

            Assert.True(Names(_driver.Blacklist, padOne));
            Assert.Equal(new[] { Dongle }, Cycled());
        }

        /// <summary>Marks the rows added by a test offline, as every row is
        /// before the poll thread's first enumeration and while the engine is
        /// stopped, with their nodes still present in Windows.</summary>
        private void NoRowIsOnlineYet(params string[] presentIds)
        {
            var present = new HashSet<string>(presentIds, StringComparer.OrdinalIgnoreCase);
            HidHideController.PresenceSeam = present.Contains;
            foreach (var ud in SettingsManager.UserDevices.Items) ud.IsOnline = false;
        }

        /// <summary>The apply that follows an engine start runs before the
        /// poll thread has marked any row online, and so does every apply
        /// while the engine is stopped. A dongle with two pads is still two
        /// controllers then, whichever of its rows the engine has reached,
        /// and it is not cycled. Engine start with Keep Devices Cloaked
        /// Between Launches off: the list is cleared and hidden again.</summary>
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void AReceiverWithTwoPadsIsNotCycledBeforeItsRowsAreOnline(bool padOneOnline, bool padTwoOnline)
        {
            string padOne = TwoRowsOnOneReceiver(InputDeviceType.Gamepad);
            NoRowIsOnlineYet(padOne, HidHideController.DevicePathToInstanceId(PadTwoPath));
            SettingsManager.UserDevices.Items[1].IsOnline = padOneOnline;
            SettingsManager.UserDevices.Items[2].IsOnline = padTwoOnline;

            HidHideController.ClearAll();
            _svc.ApplyDeviceHiding();

            Assert.True(Names(_driver.Blacklist, padOne));
            Assert.Empty(Cycled());
            lock (_releaseLines)
                Assert.Single(_releaseLines, l => l == $"HIDHIDE release skipped {padOne}: the USB device carries more than one controller");
        }

        /// <summary>A pad alone on its USB device is still reconnected by an
        /// apply that runs before its row is online: its node is present, and
        /// it counts as the one controller there.</summary>
        [Fact]
        public void AControllerAloneIsStillCycledBeforeItsRowIsOnline()
        {
            string padOne = TwoRowsOnOneReceiver(InputDeviceType.Touchpad);
            NoRowIsOnlineYet(padOne, HidHideController.DevicePathToInstanceId(PadTwoPath));

            _svc.ApplyDeviceHiding();

            Assert.Equal(new[] { Dongle }, Cycled());
        }

        // A composite USB controller whose keyboard collection PadForge shows
        // as a row of its own: the pad's collection on interface 0, the
        // keyboard's on interface 1.
        private const string PadPath = @"\\?\HID#VID_F00D&PID_0B02&MI_00#8&aa01&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string KeysPath = @"\\?\HID#VID_F00D&PID_0B02&MI_01#8&aa02&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string PadFunction = @"USB\VID_F00D&PID_0B02&MI_00\7&aa&0&0000";
        private const string KeysFunction = @"USB\VID_F00D&PID_0B02&MI_01\7&aa&0&0001";
        private const string Composite = @"USB\VID_F00D&PID_0B02\COMPOSITE";
        private static readonly string PadId = HidHideController.DevicePathToInstanceId(PadPath);
        private static readonly string KeysId = HidHideController.DevicePathToInstanceId(KeysPath);

        /// <summary>A hidden controller with a keyboard row left visible on
        /// the same USB device, the shape of an Xbox pad with its keyboard
        /// row or a handheld's pad with its touchpad row. Hiding the pad
        /// alone covers the keyboard's interface too (it is a HID child of
        /// the same USB device), and the keyboard row keeps it out. The
        /// release resolves every node to the one USB device and records
        /// cycles. Both rows start online.</summary>
        private (UserDevice pad, UserDevice keys) AControllerWithAVisibleKeyboardRow()
        {
            var hid = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
            var usb = new Guid("36fc9e60-c465-11cf-8056-444553540000");
            var functions = new[] { new HidHideController.PnpNode(PadFunction, hid), new HidHideController.PnpNode(KeysFunction, hid) };
            (IReadOnlyList<HidHideController.PnpNode>, HidHideController.PnpNode, IReadOnlyList<HidHideController.PnpNode>)? Tree(string id)
            {
                if (string.Equals(id, PadId, StringComparison.OrdinalIgnoreCase))
                    return (new[] { functions[0] }, new HidHideController.PnpNode(Composite, usb), functions);
                if (string.Equals(id, KeysId, StringComparison.OrdinalIgnoreCase))
                    return (new[] { functions[1] }, new HidHideController.PnpNode(Composite, usb), functions);
                return null;
            }
            HidHideController.TreeSeam = Tree;
            HidHideController.ChainSeam = id =>
                string.Equals(id, PadId, StringComparison.OrdinalIgnoreCase) ? new List<string> { PadId, PadFunction, Composite }
                : string.Equals(id, KeysId, StringComparison.OrdinalIgnoreCase) ? new List<string> { KeysId, KeysFunction, Composite }
                : new List<string> { id };
            HidHideController.InterfaceSeam = id =>
                string.Equals(id, PadId, StringComparison.OrdinalIgnoreCase) ? PadPath : null;

            var pad = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "Pad",
                VendorId = 0xF00D,
                ProdId = 0x0B02,
                DevicePath = PadPath,
                CapType = InputDeviceType.Gamepad,
                HidHideEnabled = true,
                IsOnline = true,
            };
            var keys = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "Pad keyboard",
                VendorId = 0xF00D,
                ProdId = 0x0B02,
                DevicePath = KeysPath,
                CapType = InputDeviceType.Keyboard,
                IsOnline = true,
            };
            SettingsManager.UserDevices.Items.Clear();
            SettingsManager.UserDevices.Items.Add(pad);
            SettingsManager.UserDevices.Items.Add(keys);

            _svc.ControllerRelease.Resolve = (id, _, _) =>
            {
                lock (_released) _released.Add(id);
                return (Composite, null);
            };
            _svc.ControllerRelease.CyclePort = device => { lock (_cycled) _cycled.Add(device); };
            _svc.ControllerRelease.Now = () => _releaseClock;
            _svc.ControllerRelease.Schedule = (_, _) => { };
            return (pad, keys);
        }

        /// <summary>An apply that runs before any row is online hides what is
        /// set to hide and leaves a visible row's nodes alone, where it once
        /// hid them and showed them again a few seconds later. The keyboard's
        /// interface is never written to HidHide's list, not even for a
        /// moment. This is the apply after an engine start with Keep Devices
        /// Cloaked Between Launches off, and any apply while the engine is
        /// stopped, and the keep-out line says which of the two it is. It
        /// once said the engine had not listed its devices yet for a
        /// stopped engine too.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnApplyBeforeRowsAreOnlineLeavesAVisibleRowsNodesAlone(bool engineStarted)
        {
            AControllerWithAVisibleKeyboardRow();
            NoRowIsOnlineYet(PadId, KeysId);
            _svc.EngineListingSeam = () => engineStarted;
            // The release's own wait for the listing has its tests.
            _svc.ControllerRelease.RowsPending = () => false;
            HidHideController.ClearAll();
            string mark = Mark();

            _svc.ApplyDeviceHiding();

            Assert.True(Names(_driver.Blacklist, PadId));
            Assert.True(Names(_driver.Blacklist, PadFunction));
            Assert.DoesNotContain(_driver.BlacklistWrites, w => Names(w, KeysFunction));
            Assert.Contains($"kept=1 [{KeysFunction}]", Assert.Single(LogLinesAfter(mark, "HIDHIDE dev ")));
            Assert.EndsWith(engineStarted
                    ? " offline rows included, the engine has not listed its devices yet"
                    : " offline rows included, the engine is stopped",
                Assert.Single(LogLinesAfter(mark, "HIDHIDE keepout ")));
            // The pad is the one controller on its USB device, so it is
            // reconnected as an engine start reconnects it.
            Assert.Equal(new[] { Composite }, Cycled());
        }

        /// <summary>With Keep Devices Cloaked Between Launches on, the list
        /// is as the last session left it. The apply after an engine start
        /// finds every entry in place before any row is online, adds nothing
        /// and reconnects nothing, and the apply after the first enumeration
        /// changes nothing either. It once added the visible keyboard row's
        /// interface, cycled the controller's port for it, and took the entry
        /// back three seconds later, at every start.</summary>
        [Fact]
        public void WithTheListKeptAnEngineStartAddsNothingAndReconnectsNothing()
        {
            var (pad, keys) = AControllerWithAVisibleKeyboardRow();
            _svc.ApplyDeviceHiding();
            Assert.Equal(new[] { Composite }, Cycled());
            Assert.True(Names(_driver.Blacklist, PadId));
            Assert.False(Names(_driver.Blacklist, KeysFunction));

            // The next engine start, more than a cooldown later: the list
            // was kept, and no row is online yet.
            NoRowIsOnlineYet(PadId, KeysId);
            _releaseClock += HiddenControllerRelease.CooldownMs;
            int writes = _driver.BlacklistWrites.Count;
            int asked = Released().Length;
            string mark = Mark();
            _vm.Settings.KeepHidHideCloaksBetweenLaunches = true;
            _svc.ApplyDeviceHidingAtEngineStart();

            Assert.Equal(writes, _driver.BlacklistWrites.Count);
            Assert.Equal(asked, Released().Length);
            Assert.Single(Cycled());
            Assert.Contains("added=0 removed=0", Assert.Single(LogLinesAfter(mark, "HIDHIDE sync ")));

            // The first enumeration marks both rows online.
            pad.IsOnline = true;
            keys.IsOnline = true;
            _svc.ApplyDeviceHiding();

            Assert.Equal(writes, _driver.BlacklistWrites.Count);
            Assert.Single(Cycled());
            Assert.False(Names(_driver.Blacklist, KeysFunction));
        }

        /// <summary>Once the engine has listed its devices, an offline row is
        /// a memory and keeps nothing out, as before (#391). Before that a
        /// row left visible only holds new hides back and takes none away:
        /// the keyboard's interface went on the list in a session where its
        /// row was offline, and an engine start with the list kept neither
        /// shows it for a few seconds nor reconnects the controller for it
        /// afterward.</summary>
        [Fact]
        public void BeforeRowsAreCurrentAVisibleRowTakesNoHideAway()
        {
            var (pad, keys) = AControllerWithAVisibleKeyboardRow();
            keys.IsOnline = false;
            _svc.RowsCurrentSeam = () => true;
            string mark = Mark();
            _svc.ApplyDeviceHiding();
            Assert.True(Names(_driver.Blacklist, KeysFunction));
            Assert.Empty(LogLinesAfter(mark, "HIDHIDE keepout "));
            Assert.Single(Cycled());

            // The next engine start with the list kept: no row is online and
            // the engine has listed nothing yet.
            NoRowIsOnlineYet(PadId, KeysId);
            _svc.RowsCurrentSeam = () => false;
            _releaseClock += HiddenControllerRelease.CooldownMs;
            int writes = _driver.BlacklistWrites.Count;
            _svc.ApplyDeviceHiding();
            Assert.Equal(writes, _driver.BlacklistWrites.Count);
            Assert.True(Names(_driver.Blacklist, KeysFunction));

            // The engine lists its devices, and the keyboard row stays offline.
            pad.IsOnline = true;
            _svc.RowsCurrentSeam = () => true;
            _svc.ApplyDeviceHiding();
            Assert.Equal(writes, _driver.BlacklistWrites.Count);
            Assert.Single(Cycled());
        }

        /// <summary>Whether the rows are current is the engine's to say: its
        /// poll thread has finished a device enumeration in this run. A new
        /// run reads false until its own loop has enumerated, with nothing to
        /// reset, and a loop that outlived its run moves nothing. Step 1
        /// notes the enumeration before it raises DevicesUpdated, so the
        /// apply that event queues reads it. The apply asks the engine, and
        /// so does the release. No engine runs in these tests, so that
        /// wiring is read from the source.</summary>
        [Fact]
        public void TheEngineSaysWhenItsRowsAreCurrent()
        {
            var im = new InputManager();
            var running = typeof(InputManager).GetField("_running",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(running);
            Assert.False(im.DevicesEnumerated);
            try
            {
                int run = im.StampRun();
                running.SetValue(im, true);
                Assert.False(im.DevicesEnumerated);
                // A thread that is no poll loop, then the loop of an earlier
                // run.
                im.NoteDevicesEnumerated(0);
                im.NoteDevicesEnumerated(run - 1);
                Assert.False(im.DevicesEnumerated);
                im.NoteDevicesEnumerated(run);
                Assert.True(im.DevicesEnumerated);

                // Stop: nothing is listed while the manager is stopped.
                running.SetValue(im, false);
                im.RunGenerationForTest = run + 1;
                Assert.False(im.DevicesEnumerated);

                // The next run has listed nothing yet, whatever the last
                // run's loop still notes on its way out.
                int next = im.StampRun();
                running.SetValue(im, true);
                Assert.False(im.DevicesEnumerated);
                im.NoteDevicesEnumerated(run);
                Assert.False(im.DevicesEnumerated);
                im.NoteDevicesEnumerated(next);
                Assert.True(im.DevicesEnumerated);
                im.NoteDevicesEnumerated(run);
                Assert.True(im.DevicesEnumerated);
            }
            finally
            {
                // The manager never started, so it leaves the test reading
                // as stopped.
                running.SetValue(im, false);
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string input = Path.Combine(dir.FullName, "PadForge.App", "Common", "Input");
            string manager = File.ReadAllText(Path.Combine(input, "InputManager.cs"));
            string step1 = File.ReadAllText(Path.Combine(input, "InputManager.Step1.UpdateDevices.cs"));
            string svc = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Services", "InputService.cs"));

            // The poll loop carries its run's stamp to Step 1.
            int loop = manager.IndexOf("private void PollingLoop(int generation, int pollRun)", StringComparison.Ordinal);
            int stamp = manager.IndexOf("t_runStamp = generation;", loop, StringComparison.Ordinal);
            int firstPass = manager.IndexOf("UpdateDevices();", loop, StringComparison.Ordinal);
            Assert.True(loop > 0 && stamp > loop && firstPass > stamp);

            int update = step1.IndexOf("private void UpdateDevices()", StringComparison.Ordinal);
            int note = step1.IndexOf("NoteDevicesEnumerated(t_runStamp);", update, StringComparison.Ordinal);
            int raise = step1.IndexOf("DevicesUpdated?.Invoke(this, EventArgs.Empty);", update, StringComparison.Ordinal);
            Assert.True(update > 0 && note > update && raise > note);

            Assert.Contains("=> RowsCurrentSeam?.Invoke() ?? (_inputManager?.DevicesEnumerated == true);", svc);
            Assert.Contains("bool rowsCurrent = RowsAreCurrent();", svc);
            Assert.Contains("=> EngineListingSeam?.Invoke() ?? (_inputManager is { IsRunning: true, DevicesEnumerated: false });", svc);
            Assert.Contains("_controllerRelease.RowsPending = EngineIsListing;", svc);
        }

        /// <summary>Two rows can name one node: the pads of a GameCube
        /// adapter, or two pads of one model remembered on the same port.
        /// Before the engine has listed its devices nothing says which of
        /// them is there, so a row left visible holds the hide off the node,
        /// and the node is not hidden for the seconds it would take the
        /// listing to show a visible row live on it. Once the engine has the
        /// hidden row online and the other is a memory, the node is hidden
        /// and the controller is reconnected.</summary>
        [Fact]
        public void BeforeRowsAreCurrentAVisibleRowHoldsANodeAHiddenRowNamesToo()
        {
            var (pad, twin) = AControllerWithAVisibleKeyboardRow();
            twin.DevicePath = PadPath;
            twin.CapType = InputDeviceType.Gamepad;
            NoRowIsOnlineYet(PadId);

            _svc.ApplyDeviceHiding();
            Assert.DoesNotContain(_driver.BlacklistWrites, w => Names(w, PadId));
            Assert.Empty(Cycled());

            // The engine lists its devices, and the hidden row is the one
            // online.
            pad.IsOnline = true;
            _svc.RowsCurrentSeam = () => true;
            _svc.ApplyDeviceHiding();
            Assert.True(Names(_driver.Blacklist, PadId));
            Assert.True(Names(_driver.Blacklist, PadFunction));
            Assert.Equal(new[] { Composite }, Cycled());
        }

        /// <summary>The same with Keep Devices Cloaked Between Launches on,
        /// where the visible row is the live one. The last session left the
        /// shared node off the list, and the apply after an engine start
        /// finds no row online, adds nothing and reconnects nothing. It once
        /// hid the node, cycled the port for it and showed it again when the
        /// rows came online, at every start.</summary>
        [Fact]
        public void WithTheListKeptARowLeftVisibleOnTheSamePathIsNotHiddenAtStart()
        {
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "Row left visible on the remote's path",
                VendorId = 0xF00D,
                ProdId = 0x0484,
                DevicePath = RemotePath,
                CapType = InputDeviceType.Keyboard,
                IsOnline = true,
            });
            ReleaseCyclesTheRemote();
            _svc.RowsCurrentSeam = () => true;
            _svc.ApplyDeviceHiding();
            Assert.False(Names(_driver.Blacklist, RemoteId));
            Assert.Empty(Cycled());

            // The next engine start: the list was kept, no row is online and
            // the engine has listed nothing yet.
            foreach (var ud in SettingsManager.UserDevices.Items) ud.IsOnline = false;
            _svc.RowsCurrentSeam = null;
            int writes = _driver.BlacklistWrites.Count;
            _svc.ApplyDeviceHiding();

            Assert.Equal(writes, _driver.BlacklistWrites.Count);
            Assert.False(Names(_driver.Blacklist, RemoteId));
            Assert.Empty(Cycled());
        }

        /// <summary>A refusal can be the inverse application cloak's own
        /// doing, which HidHide applies at every open and which no whitelist
        /// write answers. When an apply reads the cloak off, the refusal
        /// lifts, PadForge hides again and checks afresh, and the refusal
        /// line goes.</summary>
        [Fact]
        public void ARefusalTheInverseCloakExplainsLiftsWhenTheCloakGoesOff()
        {
            _openAnswer = (false, ERROR_ACCESS_DENIED);
            _svc.ApplyDeviceHiding();
            Assert.Equal(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);

            _driver.Inverse = true;
            _svc.ApplyDeviceHiding();

            _driver.Inverse = false;
            _openAnswer = (true, 0);
            string mark = Mark();
            _svc.ApplyDeviceHiding();

            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(2, _opened.Count);
            Assert.NotEqual(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);
            Assert.Single(LogLinesAfter(mark, "HIDHIDE inverse application cloak is off"));
        }

        /// <summary>A refusal that still holds after the cloak goes off
        /// latches again with the list as it then stands.</summary>
        [Fact]
        public void ARefusalThatStillHoldsLatchesAgainAfterTheCloak()
        {
            _openAnswer = (false, ERROR_ACCESS_DENIED);
            _svc.ApplyDeviceHiding();
            _driver.Inverse = true;
            _svc.ApplyDeviceHiding();
            _driver.Inverse = false;
            _svc.ApplyDeviceHiding();

            Assert.Equal(2, _opened.Count);
            Assert.False(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(Strings.Instance.Status_HidHideRefusedPadForge, _vm.StatusText);

            _svc.ApplyDeviceHiding();
            Assert.Equal(2, _opened.Count);
        }

        /// <summary>The cloak's line leaves with the cloak, hiding resumes,
        /// and the next time the cloak comes on it is announced again.</summary>
        [Fact]
        public void TheInverseNoticeLeavesWithTheCloakAndReturnsWithIt()
        {
            _driver.Inverse = true;
            _svc.ApplyDeviceHiding();
            Assert.Equal(Strings.Instance.Status_HidHideInverse, _vm.StatusText);

            _driver.Inverse = false;
            _svc.ApplyDeviceHiding();
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(string.Empty, _vm.StatusText);

            _vm.StatusText = string.Empty;
            _driver.Inverse = true;
            _svc.ApplyDeviceHiding();
            Assert.Equal(Strings.Instance.Status_HidHideInverse, _vm.StatusText);
            Assert.False(Names(_driver.Blacklist, RemoteId));
        }

        /// <summary>The cloak going off takes back only its own line: a
        /// message written since stays.</summary>
        [Fact]
        public void TheCloakGoingOffLeavesALaterMessageAlone()
        {
            _driver.Inverse = true;
            _svc.ApplyDeviceHiding();
            _vm.SetStatus("Settings saved.", persist: true);

            _driver.Inverse = false;
            _svc.ApplyDeviceHiding();
            Assert.Equal("Settings saved.", _vm.StatusText);
        }

        /// <summary>A read of the inverse flag that fails says nothing about
        /// the flag, so the last answer stands. Read as off, it would put
        /// PadForge back on a list that still hides devices from it and take
        /// the cloak's line back while the cloak is on. The log says the flag
        /// did not read, and the cloak goes when a read says so.</summary>
        [Fact]
        public void AnInverseFlagThatStopsReadingKeepsTheLastAnswer()
        {
            string native = HidHideController.CurrentProcessNativeImagePath();
            _driver.Inverse = true;
            _svc.ApplyDeviceHiding();
            Assert.Equal(Strings.Instance.Status_HidHideInverse, _vm.StatusText);

            _driver.InverseUnsupported = true;
            string mark = Mark();
            _svc.ApplyDeviceHiding();

            Assert.False(Names(_driver.Blacklist, RemoteId));
            Assert.False(Names(_driver.Whitelist, native));
            Assert.Empty(_opened);
            Assert.Equal(Strings.Instance.Status_HidHideInverse, _vm.StatusText);
            Assert.Empty(LogLinesAfter(mark, "HIDHIDE inverse application cloak is off"));
            Assert.Single(LogLinesAfter(mark, "HIDHIDE inverse flag did not read"));

            _driver.InverseUnsupported = false;
            _driver.Inverse = false;
            _svc.ApplyDeviceHiding();
            Assert.True(Names(_driver.Blacklist, RemoteId));
            Assert.Equal(string.Empty, _vm.StatusText);
        }

        /// <summary>The cloak's line rides in the change-gated block, and a
        /// cloak that comes on while nothing is hidden leaves the empty set
        /// as it was, so the change of the flag prints the block.</summary>
        [Fact]
        public void AnInverseCloakTurnedOnLaterReachesTheLog()
        {
            SettingsManager.UserDevices.Items[0].HidHideEnabled = false;
            _svc.ApplyDeviceHiding();

            _driver.Inverse = true;
            string mark = Mark();
            _svc.ApplyDeviceHiding();

            Assert.Single(LogLinesAfter(mark, "HIDHIDE inverse application cloak is on"));
        }

        /// <summary>Under the cloak a removal of PadForge's own entry that
        /// failed leaves PadForge on a list that hides devices from it. It is
        /// trouble, and it reaches the log on an apply that changes nothing
        /// else.</summary>
        [Fact]
        public void AFailedSelfRemovalUnderTheInverseCloakReachesTheLog()
        {
            SettingsManager.UserDevices.Items[0].HidHideEnabled = false;
            _driver.Inverse = true;
            _svc.ApplyDeviceHiding();

            _driver.RefuseWhitelistReads = true;
            string mark = Mark();
            _svc.ApplyDeviceHiding();

            Assert.Single(LogLinesAfter(mark, "(cleared=False)"));
        }

        /// <summary>A hide held back while PadForge's whitelist entry is
        /// unconfirmed was never written, so it is named as held, not read
        /// back as a write the driver dropped.</summary>
        [Fact]
        public void AHeldBackHideIsNotReadBackAsMissing()
        {
            _driver.RefuseWhitelistReads = true;
            string mark = Mark();
            _svc.ApplyDeviceHiding();

            string line = Assert.Single(LogLinesAfter(mark, "HIDHIDE sync "));
            Assert.DoesNotContain("readback=MISSING", line);
            Assert.Contains($"held=1 [{RemoteId}]", line);
            Assert.Contains("readback=ok", line);
        }

        /// <summary>The refusal's line is taken back as it was written. A
        /// language change in between would make the current text differ, and
        /// a compare with the current text left the line up.</summary>
        [Fact]
        public void TheRefusalLineClearsAfterALanguageChange()
        {
            var before = System.Threading.Thread.CurrentThread.CurrentUICulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en");
                _openAnswer = (false, ERROR_ACCESS_DENIED);
                _svc.ApplyDeviceHiding();
                string written = _vm.StatusText;
                Assert.Equal(Strings.Instance.Status_HidHideRefusedPadForge, written);

                System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("de");
                Assert.NotEqual(written, Strings.Instance.Status_HidHideRefusedPadForge);
                _openAnswer = (true, 0);
                _driver.Whitelist.Add(@"\Device\HarddiskVolume9\Games\other.exe");
                _svc.ApplyDeviceHiding();

                Assert.Equal(string.Empty, _vm.StatusText);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentUICulture = before;
            }
        }

        // ── PadForge's own virtual controllers, and a real pad over USB/IP ──

        private static HidHideController.OwnerHop Node(string id, string service, string manufacturer, params string[] hardwareIds)
            => new(id, hardwareIds, service, manufacturer);

        /// <summary>The walk from a HID collection up to the usbip-win2
        /// emulated host controller four hops above it, the shape measured
        /// for a composite persona (InputManager.Step1.UsbipVhciGuard). A
        /// real pad attached through usbip-win2 sits the same way. HIDMaestro
        /// adds ROOT\HIDMAESTRO_UDE to the host controller's hardware ids
        /// (UsbipDriverInstaller.StampOwnerHardwareId), and its service is
        /// usbip2_ude. <paramref name="usbLeaf"/> is the last part of the USB
        /// device's id: a number Windows makes from the port for a device
        /// with no serial, or the serial the device serves.</summary>
        private static List<HidHideController.OwnerHop> OverUsbip(string vidPid, bool stamped, bool serviceReads,
            string usbLeaf = PortMadeId) => new()
        {
            Node($@"HID\{vidPid}&MI_03\8&1&0&0000", null, null, $@"HID\{vidPid}&MI_03"),
            Node($@"USB\{vidPid}&MI_03\7&2&0&0003", "HidUsb", null, $@"USB\{vidPid}&MI_03"),
            Node($@"USB\{vidPid}\{usbLeaf}", "usbccgp", null, $@"USB\{vidPid}"),
            Node(@"USB\ROOT_HUB30\5&4&0&0", "USBHUB3", null, @"USB\ROOT_HUB30"),
            Node(@"ROOT\USB\0000", serviceReads ? "usbip2_ude" : null, null,
                stamped ? new[] { @"ROOT\USBIP_WIN2\UDE", @"ROOT\HIDMAESTRO_UDE" } : new[] { @"ROOT\USBIP_WIN2\UDE" }),
        };

        private const string PortMadeId = "6&3&0&1";

        // HIDMaestro's own serial, HM and twelve hex digits
        // (DeviceIdentity.SyntheticSerial).
        private const string PersonaSerial = "HM0123456789AB";

        /// <summary>Answers a walk for any node of the trees: the hops from
        /// that node up. A node of no tree is not present.</summary>
        private static void WalksOf(params List<HidHideController.OwnerHop>[] trees)
            => HidHideController.OwnerChainSeam = id =>
            {
                foreach (var tree in trees)
                {
                    int at = tree.FindIndex(hop => string.Equals(hop.InstanceId, id, StringComparison.OrdinalIgnoreCase));
                    if (at >= 0) return tree.Skip(at).ToList();
                }
                return null;
            };

        /// <summary>The usbip-win2 host controller as HIDMaestro has stamped
        /// it, or as usbip-win2 installs it.</summary>
        private static HidHideController.OwnerHop UsbipHost(bool stamped) => Node(@"ROOT\USB\0000", "usbip2_ude", null,
            stamped ? new[] { @"ROOT\USBIP_WIN2\UDE", @"ROOT\HIDMAESTRO_UDE" } : new[] { @"ROOT\USBIP_WIN2\UDE" });

        private static HidHideController.OwnerHop RootHub(string instance = @"5&4&0&0")
            => Node(@"USB\ROOT_HUB30\" + instance, "USBHUB3", null, @"USB\ROOT_HUB30");

        // A real port: the host controller Windows drives itself.
        private static HidHideController.OwnerHop PciHost => Node(@"PCI\VEN_8086&DEV_A36D\3&1&0&A0", "USBXHCI", null, @"PCI\VEN_8086&DEV_A36D");

        /// <summary>A pad Windows' Xbox 360 driver serves: the XInput HID
        /// node under the USB device xusb22 drives, which is where the XUSB
        /// interface sits (VendorUsbDriverInstaller.XusbInterfaceGuid).</summary>
        private static List<HidHideController.OwnerHop> Xbox360Pad(string vidPid, string port, HidHideController.OwnerHop hub,
            HidHideController.OwnerHop host) => new()
        {
            Node($@"HID\{vidPid}&IG_00\8&{port}&0&0000", null, null, $@"HID\{vidPid}&IG_00"),
            Node($@"USB\{vidPid}\6&{port}&0&1", "xusb22", null, $@"USB\{vidPid}"),
            hub,
            host,
        };

        /// <summary>A pad xboxgip serves: the XInput HID node, the node
        /// xboxgip makes for it, and the USB device, as the SDL fork walks it
        /// for a pad's identity (SDL_xinput_paddle_identity.cpp, ResolveCore:
        /// the interface's node is the HID node, the IG node is above it and
        /// the USB device above that).</summary>
        private static List<HidHideController.OwnerHop> XboxGipPad(string vidPid, HidHideController.OwnerHop hub,
            HidHideController.OwnerHop host) => new()
        {
            Node($@"HID\{vidPid}&IG_00\9&1&0&0000", null, null, $@"HID\{vidPid}&IG_00"),
            Node($@"USB\{vidPid}&IG_00\00&00&0000AA06FF8EED7E", "xinputhid", null, $@"USB\{vidPid}&IG_00"),
            Node($@"USB\{vidPid}\3032363030303236", "xboxgip", null, $@"USB\{vidPid}"),
            hub,
            host,
        };

        /// <summary>The usbip-win2 host controller carries every device
        /// attached through it, so its stamp and its service name the
        /// transport and not the owner. A real pad attached that way is
        /// HIDMaestro's only while a persona of its vendor and product id
        /// can be PadForge's own.
        ///
        /// <para>Whether the vendor and product id lookup offers it is the
        /// readers' to say. Under a host controller HIDMaestro has stamped,
        /// SDL drops the pad's HID interface, so PadForge shows no row for
        /// it and the lookup leaves it out. It once offered it there: a
        /// local pad of the model then counted as one of two and lost its
        /// hide, or the attached pad was hidden through the local pad's
        /// row. Under a host controller with no stamp PadForge reads the
        /// pad, and the lookup offers it like any other.</para></summary>
        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void ARealPadOverUsbipIsNotTakenForAPersona(bool stamped, bool serviceReads)
        {
            const string pad = @"HID\VID_F00D&PID_0CE6&MI_03\8&1&0&0000";
            WalksOf(OverUsbip("VID_F00D&PID_0CE6", stamped, serviceReads));
            var asked = new List<(ushort vendorId, ushort productId)>();
            bool mine = false;
            HidHideController.PersonaScopeSeam = (vendorId, productId) =>
            {
                asked.Add((vendorId, productId));
                return mine;
            };

            Assert.False(HidHideController.IsHidMaestroDeviceInstance(pad));
            Assert.Equal(((ushort)0xF00D, (ushort)0x0CE6), Assert.Single(asked));
            Assert.Equal(stamped, HidHideController.IsOutsideVidPidLookup(pad));
            Assert.Equal(stamped, HidHideController.IsOutsideVidPidLookup(@"USB\VID_F00D&PID_0CE6&MI_03\7&2&0&0003"));

            // The same tree with a persona of that model PadForge's own.
            mine = true;
            Assert.True(HidHideController.IsHidMaestroDeviceInstance(pad));
            Assert.True(HidHideController.IsOutsideVidPidLookup(pad));
        }

        private static bool NoXInputNode(string id) =>
            throw new Xunit.Sdk.XunitException("asked where the XUSB interface sits for a node that is no XInput interface: " + id);

        /// <summary>SDL drops a HID interface when a hardware id of its
        /// node or one of its four parents names HIDMaestro
        /// (HM_FILTER_MAX_DEPTH 5, hid.c). The stamped host controller is
        /// four parents above the HID node of a composite device attached
        /// through usbip-win2 and three above a single-interface device's,
        /// so PadForge reads neither. A node that is no HID node, the USB
        /// interface above a collection, follows the HID node below it. One
        /// hub deeper the walk ends at the root hub, and PadForge reads the
        /// pad.</summary>
        [Fact]
        public void AHidNodeIsDroppedWhereSdlsWalkReachesTheStamp()
        {
            var composite = OverUsbip("VID_F00D&PID_0CE6", stamped: true, serviceReads: true);
            Assert.True(HidHideController.ReadersDrop(composite, NoXInputNode));
            Assert.True(HidHideController.ReadersDrop(composite.Skip(1).ToList(), NoXInputNode));

            var single = new List<HidHideController.OwnerHop>
            {
                Node(@"HID\VID_F00D&PID_0005\7&2&0&0000", null, null, @"HID\VID_F00D&PID_0005"),
                Node(@"USB\VID_F00D&PID_0005\6&3&0&1", "HidUsb", null, @"USB\VID_F00D&PID_0005"),
                RootHub(),
                UsbipHost(stamped: true),
            };
            Assert.True(HidHideController.ReadersDrop(single, NoXInputNode));
            Assert.True(HidHideController.ReadersDrop(single.Skip(1).ToList(), NoXInputNode));

            // Behind a hub that is itself attached: the stamp is five
            // parents above the HID node.
            var behindAHub = new List<HidHideController.OwnerHop>
            {
                Node(@"HID\VID_F00D&PID_0CE6&MI_03\9&1&0&0000", null, null, @"HID\VID_F00D&PID_0CE6&MI_03"),
                Node(@"USB\VID_F00D&PID_0CE6&MI_03\8&2&0&0003", "HidUsb", null, @"USB\VID_F00D&PID_0CE6&MI_03"),
                Node(@"USB\VID_F00D&PID_0CE6\7&3&0&2", "usbccgp", null, @"USB\VID_F00D&PID_0CE6"),
                Node(@"USB\VID_05E3&PID_0610\6&3&0&1", "USBHUB3", null, @"USB\VID_05E3&PID_0610"),
                RootHub(),
                UsbipHost(stamped: true),
            };
            Assert.False(HidHideController.ReadersDrop(behindAHub, NoXInputNode));
            Assert.False(HidHideController.ReadersDrop(behindAHub.Skip(1).ToList(), NoXInputNode));

            // No stamp: SDL lists everything under the host controller.
            var unstamped = OverUsbip("VID_F00D&PID_0CE6", stamped: false, serviceReads: true);
            Assert.False(HidHideController.ReadersDrop(unstamped, NoXInputNode));
            Assert.False(HidHideController.ReadersDrop(unstamped.Skip(1).ToList(), NoXInputNode));
            Assert.False(HidHideController.ReadersDrop(null, NoXInputNode));
        }

        /// <summary>An XInput interface is OpenXInput's to read, and it
        /// gives no slot when a hardware id of the XUSB interface's node or
        /// one of its four parents names HIDMaestro (maxDepth 5,
        /// OpenXinput.cpp). On a pad xusb22 serves the interface sits on
        /// the USB device above the HID node, two nodes below the host
        /// controller. On a pad xboxgip serves it sits on the HID node,
        /// four below. The walk reaches the stamp from both, so both are
        /// dropped, and the node xboxgip makes above the HID node follows
        /// it. A walk of four nodes ended at the root hub and left the pad
        /// xboxgip serves its slot, which is how PadForge came to list its
        /// own Xbox Series controller as XInput#0 (hifihedgehog/SDL#51).
        /// One hub more between the pad and the host controller is past
        /// the walk.</summary>
        [Fact]
        public void AnXInputNodeIsDroppedWhereOpenXInputsWalkReachesTheStamp()
        {
            var wired360 = Xbox360Pad("VID_F00D&PID_028E", "3", RootHub(), UsbipHost(stamped: true));
            Assert.True(HidHideController.ReadersDrop(wired360, _ => false));
            // Were the interface on the HID node itself, the walk would
            // still reach: the stamp is three parents above it.
            Assert.True(HidHideController.ReadersDrop(wired360, _ => true));

            var gip = XboxGipPad("VID_F00D&PID_0B12", RootHub(), UsbipHost(stamped: true));
            string gipHid = gip[0].InstanceId;
            Func<string, bool> onTheHidNode = id => string.Equals(id, gipHid, StringComparison.OrdinalIgnoreCase);
            Assert.True(HidHideController.ReadersDrop(gip, onTheHidNode));
            Assert.True(HidHideController.ReadersDrop(gip.Skip(1).ToList(), onTheHidNode));
            // Were the interface one node up, the stamp would be nearer
            // still, and both nodes would be dropped as well.
            string gipAbove = gip[1].InstanceId;
            Func<string, bool> onTheNodeAbove = id => string.Equals(id, gipAbove, StringComparison.OrdinalIgnoreCase);
            Assert.True(HidHideController.ReadersDrop(gip, onTheNodeAbove));
            Assert.True(HidHideController.ReadersDrop(gip.Skip(1).ToList(), onTheNodeAbove));

            // The same pad behind one more hub: the stamp is five nodes
            // above the interface, one past the walk.
            var behindAHub = new List<HidHideController.OwnerHop>(gip.Take(3))
            {
                Node(@"USB\VID_05E3&PID_0610\6&3&0&1", "USBHUB3", null, @"USB\VID_05E3&PID_0610"),
                RootHub(),
                UsbipHost(stamped: true),
            };
            Assert.False(HidHideController.ReadersDrop(behindAHub, onTheHidNode));
            Assert.False(HidHideController.ReadersDrop(behindAHub.Skip(1).ToList(), onTheHidNode));

            // No stamp, no drop.
            Assert.False(HidHideController.ReadersDrop(
                Xbox360Pad("VID_F00D&PID_028E", "3", RootHub(), UsbipHost(stamped: false)), _ => false));
        }

        /// <summary>The lookup by vendor and product id offers the nodes
        /// of real devices PadForge reads: a pad on a real port, and a pad
        /// attached through usbip-win2 under a host controller with no
        /// stamp. It leaves out a persona, known by its serial, and a pad
        /// under a stamped host controller that SDL drops.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheLookupOffersOnlyWhatPadForgeReads(bool stamped)
        {
            var local = new List<HidHideController.OwnerHop>
            {
                Node(@"HID\VID_F00D&PID_0CE6&MI_03\A&1&0&0000", null, null, @"HID\VID_F00D&PID_0CE6&MI_03"),
                Node(@"USB\VID_F00D&PID_0CE6&MI_03\9&2&0&0003", "HidUsb", null, @"USB\VID_F00D&PID_0CE6&MI_03"),
                Node(@"USB\VID_F00D&PID_0CE6\8&3&0&4", "usbccgp", null, @"USB\VID_F00D&PID_0CE6"),
                RootHub("4&1&0&0"),
                PciHost,
            };
            var attached = OverUsbip("VID_F00D&PID_0CE6", stamped, serviceReads: true);
            var persona = new List<HidHideController.OwnerHop>
            {
                Node(@"HID\VID_F00D&PID_0CE6&MI_03\B&1&0&0000", null, null, @"HID\VID_F00D&PID_0CE6&MI_03"),
                Node(@"USB\VID_F00D&PID_0CE6&MI_03\A&2&0&0003", "HidUsb", null, @"USB\VID_F00D&PID_0CE6&MI_03"),
                Node(@"USB\VID_F00D&PID_0CE6\" + PersonaSerial, "usbccgp", null, @"USB\VID_F00D&PID_0CE6"),
                RootHub(),
                UsbipHost(stamped),
            };
            WalksOf(local, attached, persona);
            HidHideController.PersonaScopeSeam = (_, _) => false;
            HidHideController.PresentHidNodesSeam = () => new[]
            {
                persona[0].InstanceId, persona[1].InstanceId,
                attached[0].InstanceId, attached[1].InstanceId,
                local[0].InstanceId, local[1].InstanceId,
                @"HID\VID_F00D&PID_0001\7&1&0&0000",
            };

            var offered = HidHideController.FindInstanceIdsByVidPid(0xF00D, 0x0CE6);

            var expected = new List<string> { local[0].InstanceId, local[1].InstanceId };
            if (!stamped)
            {
                expected.Add(attached[0].InstanceId);
                expected.Add(attached[1].InstanceId);
            }
            expected.Sort(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(expected, offered);
        }

        private const string LocalPadPath = "XInput#0";

        /// <summary>A local XInput pad set to hide, and a second pad of
        /// the model attached from another machine through usbip-win2 under
        /// a host controller HIDMaestro has stamped. SDL reads the local
        /// pad through XInput and never lists the attached one, so the
        /// local pad's row is the only row. Returns that row and the HID
        /// node of each pad.</summary>
        private (UserDevice row, string localId, string attachedId) ALocalPadAndATwinAttachedOverUsbip()
        {
            var local = Xbox360Pad("VID_F00D&PID_028E", "7", RootHub("4&1&0&0"), PciHost);
            var attached = Xbox360Pad("VID_F00D&PID_028E", "3", RootHub(), UsbipHost(stamped: true));
            WalksOf(local, attached);
            HidHideController.PersonaScopeSeam = (_, _) => false;
            HidHideController.XusbInterfaceSeam = _ => false;
            HidHideController.PresentHidNodesSeam = () => new[] { attached[0].InstanceId, local[0].InstanceId };
            string localId = local[0].InstanceId;
            HidHideController.InterfaceSeam = id =>
                string.Equals(id, localId, StringComparison.OrdinalIgnoreCase)
                    ? @"\\?\" + localId.Replace('\\', '#') + "#{4d1e55b2-f16f-11cf-88cb-001111000030}"
                    : null;

            var row = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "Local pad",
                VendorId = 0xF00D,
                ProdId = 0x028E,
                DevicePath = LocalPadPath,
                CapType = InputDeviceType.Gamepad,
                HidHideEnabled = true,
                IsOnline = true,
            };
            SettingsManager.UserDevices.Items.Clear();
            SettingsManager.UserDevices.Items.Add(row);
            _svc.RowsCurrentSeam = () => true;
            return (row, localId, attached[0].InstanceId);
        }

        /// <summary>The attached pad has no row, and no other pad's row
        /// hides it: the local pad's hide takes the local pad's node alone.
        /// The lookup once offered both nodes, and the local row hid the
        /// attached pad and kept its node.</summary>
        [Fact]
        public void APadPadForgeCannotReadIsNotHiddenThroughAnotherPadsRow()
        {
            var (row, localId, attachedId) = ALocalPadAndATwinAttachedOverUsbip();

            _svc.ApplyDeviceHiding();

            Assert.True(Names(_driver.Blacklist, localId));
            Assert.DoesNotContain(_driver.BlacklistWrites, w => Names(w, attachedId));
            Assert.Equal(new[] { localId }, row.HidHideInstanceIds);
        }

        /// <summary>The same two pads with their containers read: the gate
        /// counts the devices of the model PadForge reads, which is one, so
        /// the local pad keeps its hide. It once counted the attached pad
        /// as a twin and hid nothing.</summary>
        [Fact]
        public void APadPadForgeCannotReadDoesNotCountAsATwin()
        {
            var (row, localId, attachedId) = ALocalPadAndATwinAttachedOverUsbip();

            bool sweep = InputService.HidHideSiblingSweepAllowed(new[] { row }, row, out int same, out int devices,
                _ => true, null,
                id => string.Equals(id, localId, StringComparison.OrdinalIgnoreCase) ? "local"
                    : string.Equals(id, attachedId, StringComparison.OrdinalIgnoreCase) ? "attached" : null);

            Assert.True(sweep);
            Assert.Equal(1, same);
            Assert.Equal(1, devices);
        }

        /// <summary>A node the lookup no longer offers leaves the record
        /// that kept it: an attached pad's node a local pad's record took
        /// while the lookup offered it is dropped and shown again.</summary>
        [Fact]
        public void ANodeTheLookupLeavesOutIsDroppedFromTheRecordThatKeptIt()
        {
            var (row, localId, attachedId) = ALocalPadAndATwinAttachedOverUsbip();
            row.HidHideInstanceIds.Add(attachedId);
            _driver.Blacklist.Add(attachedId);
            Assert.Equal(1, HidHideController.AdoptExistingAsManaged());

            _svc.ApplyDeviceHiding();

            Assert.Equal(new[] { localId }, row.HidHideInstanceIds);
            Assert.True(Names(_driver.Blacklist, localId));
            Assert.False(Names(_driver.Blacklist, attachedId));
        }

        /// <summary>A composite persona serves HIDMaestro's own serial unless
        /// its profile carries a captured one, and Windows names its USB
        /// device by it. Under the usbip-win2 host controller a node at or
        /// below such a USB device is a persona whatever the id scope says,
        /// as Step 1 reads it (HidMaestroAncestry.IsPersonaUsbInstanceId). The
        /// scope ends ten seconds after a persona is disposed and Windows
        /// can list the device longer, and such a persona once read as a
        /// real pad of its model here, to be hidden and reconnected. The
        /// same tree with the id Windows makes from the port is a real pad
        /// attached that way.</summary>
        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void APersonaIsKnownByItsSerialWhateverTheScope(bool stamped, bool serviceReads)
        {
            HidHideController.PersonaScopeSeam = (_, _) =>
                throw new Xunit.Sdk.XunitException("the id scope was asked about a node under HIDMaestro's serial");
            WalksOf(OverUsbip("VID_F00D&PID_0CE6", stamped, serviceReads, PersonaSerial));

            // The HID collection, its USB interface and the USB device.
            Assert.True(HidHideController.IsHidMaestroDeviceInstance(@"HID\VID_F00D&PID_0CE6&MI_03\8&1&0&0000"));
            Assert.True(HidHideController.IsHidMaestroDeviceInstance(@"USB\VID_F00D&PID_0CE6&MI_03\7&2&0&0003"));
            Assert.True(HidHideController.IsHidMaestroDeviceInstance(@"USB\VID_F00D&PID_0CE6\" + PersonaSerial));

            HidHideController.PersonaScopeSeam = (_, _) => false;
            WalksOf(OverUsbip("VID_F00D&PID_0CE6", stamped, serviceReads, PortMadeId));
            Assert.False(HidHideController.IsHidMaestroDeviceInstance(@"HID\VID_F00D&PID_0CE6&MI_03\8&1&0&0000"));
        }

        /// <summary>The serial names a persona only under the usbip-win2
        /// host controller, as in Step 1. A real device on a real port whose
        /// own serial happens to have that shape is a real device.</summary>
        [Fact]
        public void HidMaestrosSerialOnARealPortIsNoPersona()
        {
            HidHideController.PersonaScopeSeam = (_, _) =>
                throw new Xunit.Sdk.XunitException("the id scope was asked about a node on a real port");
            WalksOf(new List<HidHideController.OwnerHop>
            {
                Node(@"HID\VID_F00D&PID_0CE6&MI_03\8&1&0&0000", null, null, @"HID\VID_F00D&PID_0CE6&MI_03"),
                Node(@"USB\VID_F00D&PID_0CE6&MI_03\7&2&0&0003", "HidUsb", null, @"USB\VID_F00D&PID_0CE6&MI_03"),
                Node(@"USB\VID_F00D&PID_0CE6\" + PersonaSerial, "usbccgp", null, @"USB\VID_F00D&PID_0CE6"),
                Node(@"USB\ROOT_HUB30\5&4&0&0", "USBHUB3", null, @"USB\ROOT_HUB30"),
                Node(@"PCI\VEN_8086&DEV_A36D\3&1&0&A0", "USBXHCI", null, @"PCI\VEN_8086&DEV_A36D"),
            });

            Assert.False(HidHideController.IsHidMaestroDeviceInstance(@"HID\VID_F00D&PID_0CE6&MI_03\8&1&0&0000"));
        }

        /// <summary>The serial test is Step 1's own, not a second one: for
        /// each USB device id the two sides give one answer.</summary>
        [Theory]
        [InlineData("HM0123456789AB", true)]
        [InlineData("hm0123456789ab", true)]
        [InlineData("HM0123456789A", false)]
        [InlineData("HM0123456789ABC", false)]
        [InlineData("HM01234567G9AB", false)]
        [InlineData("HX0123456789AB", false)]
        [InlineData("0022AACCDEE8", false)]
        public void TheSerialTestIsTheOneStep1Uses(string usbLeaf, bool persona)
        {
            HidHideController.PersonaScopeSeam = (_, _) => false;
            WalksOf(OverUsbip("VID_F00D&PID_0CE6", stamped: false, serviceReads: true, usbLeaf));

            Assert.Equal(persona, HidMaestroAncestry.IsPersonaUsbInstanceId(@"USB\VID_F00D&PID_0CE6\" + usbLeaf));
            Assert.Equal(persona, HidHideController.IsHidMaestroDeviceInstance(@"HID\VID_F00D&PID_0CE6&MI_03\8&1&0&0000"));
        }

        /// <summary>The hiding side reads the host controller and the
        /// HIDMaestro ids as Step 1 reads them, so the two cannot drift: on
        /// the same hops, a node is HIDMaestro's where Step 1's walk finds a
        /// persona or a node of HIDMaestro's own driver, and where it finds
        /// the host controller alone it is HIDMaestro's exactly while the
        /// scope holds. The hops carry no marker of the hiding side's own,
        /// so the classification alone decides. That includes the walk's
        /// bound: a host controller past the ninth node is out of reach for
        /// both.</summary>
        [Fact]
        public void TheHidingSideReadsTheHostControllerAsStep1Does()
        {
            var nine = new List<HidHideController.OwnerHop>();
            for (int i = 0; i < 9; i++)
                nine.Add(Node($@"USB\VID_F00D&PID_0C{i:D2}\{i}", "USBHUB3", null, $@"USB\VID_F00D&PID_0C{i:D2}"));
            var hopLists = new List<List<HidHideController.OwnerHop>>
            {
                OverUsbip("VID_F00D&PID_0CE6", stamped: true, serviceReads: true, PersonaSerial),
                OverUsbip("VID_F00D&PID_0CE6", stamped: false, serviceReads: true, PersonaSerial),
                OverUsbip("VID_F00D&PID_0CE6", stamped: true, serviceReads: false, PersonaSerial),
                OverUsbip("VID_F00D&PID_0CE6", stamped: true, serviceReads: true),
                OverUsbip("VID_F00D&PID_0CE6", stamped: false, serviceReads: true),
                OverUsbip("VID_F00D&PID_0CE6", stamped: false, serviceReads: false),
                new() { Node(@"HID\VID_054C&PID_0CE6\1&2D9A&0&0000", null, null, @"HID\VID_054C&PID_0CE6", @"HID\HIDMaestro") },
                new()
                {
                    Node(@"HID\VID_054C&PID_0CE6\1&2D9A&0&0000", null, null, @"HID\VID_054C&PID_0CE6"),
                    Node(@"ROOT\HIDCLASS\0001", null, null, @"root\VID_054C&PID_0CE6", @"root\HIDMaestro"),
                },
                new()
                {
                    Node(@"HID\VID_F00D&PID_0CE6&MI_03\8&1&0&0000", null, null, @"HID\VID_F00D&PID_0CE6&MI_03"),
                    Node(@"ROOT\USB\0000", "usbip2_ude", null, @"ROOT\HIDMAESTRO_UDE", @"ROOT\HIDMaestro"),
                },
                new()
                {
                    Node(@"HID\VID_F00D&PID_0CE6&MI_03\8&1&0&0000", null, null, @"HID\VID_F00D&PID_0CE6&MI_03"),
                    Node(@"USB\VID_F00D&PID_0CE6\" + PersonaSerial, "usbccgp", null, @"USB\VID_F00D&PID_0CE6"),
                    RootHub(),
                    PciHost,
                },
                new List<HidHideController.OwnerHop>(nine) { UsbipHost(stamped: true) },
                new List<HidHideController.OwnerHop>(nine.Take(8)) { UsbipHost(stamped: true) },
            };

            var seen = new HashSet<UsbipAncestry>();
            foreach (var hops in hopLists)
            {
                var step1 = HidMaestroAncestry.Classify(
                    hops.Select(hop => new AncestryHop(hop.InstanceId, hop.HardwareIds, hop.Service)));
                seen.Add(step1);
                foreach (bool inScope in new[] { false, true })
                {
                    bool expected = step1 is UsbipAncestry.Persona or UsbipAncestry.HidMaestroNode
                        || (step1 == UsbipAncestry.UnderVhci && inScope);
                    Assert.True(expected == HidHideController.IsHidMaestroChain(hops, () => inScope),
                        $"{hops[0].InstanceId} under {hops[^1].InstanceId}: Step 1 reads {step1}, scope {inScope}");
                }
            }
            // Every answer Step 1's walk gives was asked about.
            Assert.Equal(Enum.GetValues<UsbipAncestry>().Length, seen.Count);
        }

        /// <summary>HIDMaestro's own devices are told by their markers,
        /// whatever the personas in scope: HIDMaestro in an instance id, in
        /// the node's own hardware ids, in a hardware id of a node above
        /// (root\HIDMaestro, which is not the host controller's stamp), or as
        /// the manufacturer.</summary>
        [Fact]
        public void HidMaestrosOwnDevicesAreToldByTheirMarkers()
        {
            static bool NotAsked() => throw new Xunit.Sdk.XunitException("the persona scope was asked for a marked device");

            // The HID child of a HIDMaestro node: HID\HIDMaestro in its own ids.
            Assert.True(HidHideController.IsHidMaestroChain(new[]
            {
                Node(@"HID\VID_054C&PID_0CE6\1&2D9A&0&0000", null, null, @"HID\VID_054C&PID_0CE6", @"HID\HIDMaestro"),
            }, NotAsked));

            // The same child with its own ids unread: root\HIDMaestro on the node above.
            Assert.True(HidHideController.IsHidMaestroChain(new[]
            {
                Node(@"HID\VID_054C&PID_0CE6\1&2D9A&0&0000", null, null),
                Node(@"ROOT\HIDCLASS\0001", null, null, @"root\VID_054C&PID_0CE6", @"root\HIDMaestro"),
            }, NotAsked));

            // Only the manufacturer reads.
            Assert.True(HidHideController.IsHidMaestroChain(new[]
            {
                Node(@"HID\VID_054C&PID_0CE6\1&2D9A&0&0000", null, null),
                Node(@"ROOT\HIDCLASS\0001", null, "HIDMaestro"),
            }, NotAsked));

            // An instance id above that names HIDMaestro's enumerator.
            Assert.True(HidHideController.IsHidMaestroChain(new[]
            {
                Node(@"HID\VID_045E&PID_028E&IG_00\1&2D9A&0&0000", null, null),
                Node(@"ROOT\VID_045E&PID_028E&IG_00\0000", null, null),
            }, NotAsked));

            // A real pad on a real port carries none of it.
            Assert.False(HidHideController.IsHidMaestroChain(new[]
            {
                Node(@"HID\VID_054C&PID_0CE6&MI_03\8&1&0&0000", null, null, @"HID\VID_054C&PID_0CE6&MI_03"),
                Node(@"USB\VID_054C&PID_0CE6&MI_03\7&2&0&0003", "HidUsb", null, @"USB\VID_054C&PID_0CE6&MI_03"),
                Node(@"USB\VID_054C&PID_0CE6\6&3&0&1", "usbccgp", null, @"USB\VID_054C&PID_0CE6"),
                Node(@"USB\ROOT_HUB30\5&4&0&0", "USBHUB3", null, @"USB\ROOT_HUB30"),
                Node(@"PCI\VEN_8086&DEV_A36D\3&1&0&A0", "USBXHCI", null, @"PCI\VEN_8086&DEV_A36D"),
            }, NotAsked));
            Assert.False(HidHideController.IsHidMaestroChain(null, NotAsked));
        }

        private static List<AncestryHop> AsAncestry(List<HidHideController.OwnerHop> tree)
            => tree.Select(hop => new AncestryHop(hop.InstanceId, hop.HardwareIds, hop.Service)).ToList();

        /// <summary>The identity of an XInput pad reads its node's walk:
        /// the tree's hops from that node up, and none for a node of no
        /// tree.</summary>
        private static Func<string, IReadOnlyList<AncestryHop>> TreeWalk(params List<HidHideController.OwnerHop>[] trees)
            => id =>
            {
                foreach (var tree in trees)
                {
                    int at = tree.FindIndex(hop => string.Equals(hop.InstanceId, id, StringComparison.OrdinalIgnoreCase));
                    if (at >= 0) return AsAncestry(tree.Skip(at).ToList());
                }
                return null;
            };

        /// <summary>One list of hops, one answer. Every reader of the rule
        /// that tells HIDMaestro's devices from real ones reads each list
        /// the hiding and Step 1 tests use, with the persona scope held and
        /// not: Step 1's walk (its classification and the scope it then
        /// asks), the hiding side (IsHidMaestroChain), the shared rule
        /// itself (HidMaestroAncestry.IsHidMaestroWalk) and the identity of
        /// an XInput pad (StableXInputInstance.IsHidMaestroInstance). None
        /// answers otherwise than the rest. The lists carry no
        /// manufacturer, the one marker the hiding side reads alone.</summary>
        [Fact]
        public void EveryReaderOfTheRuleGivesOneAnswer()
        {
            var nine = new List<HidHideController.OwnerHop>();
            for (int i = 0; i < 9; i++)
                nine.Add(Node($@"USB\VID_F00D&PID_0C{i:D2}\{i}", "USBHUB3", null, $@"USB\VID_F00D&PID_0C{i:D2}"));
            var hopLists = new List<List<HidHideController.OwnerHop>>
            {
                OverUsbip("VID_F00D&PID_0CE6", stamped: true, serviceReads: true, PersonaSerial),
                OverUsbip("VID_F00D&PID_0CE6", stamped: false, serviceReads: true, PersonaSerial),
                OverUsbip("VID_F00D&PID_0CE6", stamped: true, serviceReads: false, PersonaSerial),
                OverUsbip("VID_F00D&PID_0CE6", stamped: true, serviceReads: true),
                OverUsbip("VID_F00D&PID_0CE6", stamped: false, serviceReads: true),
                OverUsbip("VID_F00D&PID_0CE6", stamped: false, serviceReads: false),
                new() { Node(@"HID\VID_054C&PID_0CE6\1&2D9A&0&0000", null, null, @"HID\VID_054C&PID_0CE6", @"HID\HIDMaestro") },
                new()
                {
                    Node(@"HID\VID_054C&PID_0CE6\1&2D9A&0&0000", null, null, @"HID\VID_054C&PID_0CE6"),
                    Node(@"ROOT\HIDCLASS\0001", null, null, @"root\VID_054C&PID_0CE6", @"root\HIDMaestro"),
                },
                new()
                {
                    Node(@"HID\VID_F00D&PID_0CE6&MI_03\8&1&0&0000", null, null, @"HID\VID_F00D&PID_0CE6&MI_03"),
                    Node(@"ROOT\USB\0000", "usbip2_ude", null, @"ROOT\HIDMAESTRO_UDE", @"ROOT\HIDMaestro"),
                },
                new()
                {
                    Node(@"HID\VID_F00D&PID_0CE6&MI_03\8&1&0&0000", null, null, @"HID\VID_F00D&PID_0CE6&MI_03"),
                    Node(@"USB\VID_F00D&PID_0CE6\" + PersonaSerial, "usbccgp", null, @"USB\VID_F00D&PID_0CE6"),
                    RootHub(),
                    PciHost,
                },
                new List<HidHideController.OwnerHop>(nine) { UsbipHost(stamped: true) },
                new List<HidHideController.OwnerHop>(nine.Take(8)) { UsbipHost(stamped: true) },
                Xbox360Pad("VID_F00D&PID_028E", "3", RootHub(), UsbipHost(stamped: true)),
                Xbox360Pad("VID_F00D&PID_028E", "3", RootHub(), UsbipHost(stamped: false)),
                Xbox360Pad("VID_F00D&PID_028E", "7", RootHub("4&1&0&0"), PciHost),
                XboxGipPad("VID_F00D&PID_0B12", RootHub(), UsbipHost(stamped: true)),
                XboxGipPad("VID_F00D&PID_0B12", RootHub(), UsbipHost(stamped: false)),
                XboxGipPad("VID_F00D&PID_0B12", RootHub("4&1&0&0"), PciHost),
            };

            var seen = new HashSet<UsbipAncestry>();
            foreach (var tree in hopLists)
            {
                var hops = AsAncestry(tree);
                var step1Walk = HidMaestroAncestry.Classify(hops);
                seen.Add(step1Walk);
                foreach (bool inScope in new[] { false, true })
                {
                    bool step1 = step1Walk is UsbipAncestry.Persona or UsbipAncestry.HidMaestroNode
                        || (step1Walk == UsbipAncestry.UnderVhci && inScope);
                    var answers = new[]
                    {
                        ("hiding side", HidHideController.IsHidMaestroChain(tree, () => inScope)),
                        ("shared rule", HidMaestroAncestry.IsHidMaestroWalk(hops, () => inScope)),
                        ("XInput identity", StableXInputInstance.IsHidMaestroInstance(tree[0].InstanceId, () => inScope, TreeWalk(tree))),
                    };
                    foreach (var (reader, answer) in answers)
                        Assert.True(step1 == answer,
                            $"{tree[0].InstanceId} under {tree[^1].InstanceId}, scope {inScope}: Step 1 reads {step1Walk} and answers {step1}, the {reader} {answer}");
                }
            }
            // Every answer the classification gives was asked about.
            Assert.Equal(Enum.GetValues<UsbipAncestry>().Length, seen.Count);
        }

        /// <summary>
        /// What the identity of an XInput pad (StableXInputInstance) takes for
        /// a candidate node, by kind of device. It leaves out HIDMaestro's
        /// devices and the nodes OpenXInput gives no slot, since no XInput#N
        /// path names those.
        ///
        /// <para>A real pad attached over usbip-win2 under a host controller
        /// HIDMaestro has stamped gets no slot, whichever of Windows' Xbox
        /// drivers serves it: the XUSB interface of a pad xusb22 serves
        /// sits two nodes below the stamp, that of a pad xboxgip serves
        /// four, and OpenXInput's walk reaches both. No XInput#N path names
        /// such a pad, so it stays out, though it is no device of
        /// HIDMaestro's. Under a host controller with no stamp both keep
        /// their slots and stay candidates for their own rows' identities.
        /// The stamp alone never makes a pad HIDMaestro's: read that way,
        /// a pad that kept its slot had no node of its own, and it and a
        /// local pad of its model had one identity. Every one of PadForge's
        /// own virtual controllers stays out under either scope.</para>
        /// </summary>
        [Fact]
        public void TheXInputIdentityTakesAPadOverUsbipForARealOne()
        {
            bool HidMaestros(List<HidHideController.OwnerHop> tree, bool inScope)
                => StableXInputInstance.IsHidMaestroInstance(tree[0].InstanceId, () => inScope, TreeWalk(tree));
            bool LeftOut(List<HidHideController.OwnerHop> tree, bool inScope, Func<string, bool> carriesXusb)
                => StableXInputInstance.IsLeftOut(tree[0].InstanceId, () => inScope, TreeWalk(tree), carriesXusb);
            static Func<string, bool> OnTheNode(string node)
                => id => string.Equals(id, node, StringComparison.OrdinalIgnoreCase);

            foreach (bool stamped in new[] { true, false })
            {
                // xboxgip: the XUSB interface on the HID node.
                var gip = XboxGipPad("VID_045E&PID_0B12", RootHub(), UsbipHost(stamped));
                Assert.False(HidMaestros(gip, inScope: false));
                Assert.Equal(stamped, LeftOut(gip, false, OnTheNode(gip[0].InstanceId)));
                // xusb22: the XUSB interface on the USB device above it.
                var wired360 = Xbox360Pad("VID_045E&PID_028E", "3", RootHub(), UsbipHost(stamped));
                Assert.False(HidMaestros(wired360, inScope: false));
                Assert.Equal(stamped, LeftOut(wired360, false, OnTheNode(wired360[1].InstanceId)));
            }

            // Real pads on this PC: wired on a real port, and over Bluetooth.
            var wired = XboxGipPad("VID_045E&PID_0B12", RootHub("4&1&0&0"), PciHost);
            Assert.False(HidMaestros(wired, inScope: true));
            Assert.False(LeftOut(wired, true, OnTheNode(wired[0].InstanceId)));
            const string Ble = "{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02045e_PID&0b13";
            var bluetooth = new List<HidHideController.OwnerHop>
            {
                Node($@"HID\{Ble}_REV&0509_c8d73a0d23a1&IG_00\9&2&0&0000", null, null, $@"HID\{Ble}&IG_00"),
                Node($@"BTHLEDEVICE\{Ble}_REV&0509_c8d73a0d23a1\8&1&0&0", "HidBth", null, $@"BTHLEDEVICE\{Ble}"),
                Node(@"BTHLE\Dev_c8d73a0d23a1\7&1&0&BLUETOOTHDEVICE_C8D73A0D23A1", "BthLEEnum", null, @"BTHLE\GenericDevice"),
                Node(@"USB\VID_8087&PID_0033\5&1&0&10", "BTHUSB", null, @"USB\VID_8087&PID_0033"),
                RootHub("4&1&0&0"),
                PciHost,
            };
            Assert.False(HidMaestros(bluetooth, inScope: true));
            Assert.False(LeftOut(bluetooth, true, OnTheNode(bluetooth[0].InstanceId)));

            // PadForge's own: an Xbox 360 profile under the root enumerator
            // HIDMaestro makes, an Xbox Series profile under its gamepad
            // companion, a DualShock 4 profile on HIDMaestro's driver, and a
            // composite persona that serves HIDMaestro's serial under either
            // kind of host controller.
            var ownDevices = new List<List<HidHideController.OwnerHop>>
            {
                new()
                {
                    Node(@"HID\VID_045E&PID_028E&IG_00\1&2D9A&0&0000", null, null, @"HID\VID_045E&PID_028E&IG_00"),
                    Node(@"ROOT\VID_045E&PID_028E&IG_00\0000", null, null, @"root\VID_045E&PID_028E&IG_00"),
                },
                new()
                {
                    Node(@"HID\VID_045E&PID_0B13&IG_00\1&2D9A&0&0000", null, null, @"HID\VID_045E&PID_0B13&IG_00"),
                    Node(@"SWD\HMCOMPANION_VID_045E_PID_0B13\0000", null, null,
                        @"root\VID_045E&PID_0B13&IG_00", @"root\HIDMaestroGamepad", @"root\HIDMaestro"),
                },
                new() { Node(@"HID\VID_054C&PID_05C4\1&2D9A&0&0000", null, null, @"HID\VID_054C&PID_05C4", @"HID\HIDMaestro") },
                OverUsbip("VID_054C&PID_0CE6", stamped: true, serviceReads: true, PersonaSerial),
                OverUsbip("VID_054C&PID_0CE6", stamped: false, serviceReads: true, PersonaSerial),
            };
            foreach (var own in ownDevices)
                foreach (bool inScope in new[] { false, true })
                {
                    Assert.True(HidMaestros(own, inScope), own[0].InstanceId);
                    Assert.True(LeftOut(own, inScope, _ => false), own[0].InstanceId);
                }

            // A persona with a captured serial is told by its scope alone, as
            // on the hiding side and in Step 1.
            var captured = OverUsbip("VID_28DE&PID_1205", stamped: true, serviceReads: true, "FX1A00000002");
            Assert.True(HidMaestros(captured, inScope: true));
            Assert.False(HidMaestros(captured, inScope: false));
            Assert.False(StableXInputInstance.IsHidMaestroInstance(@"HID\VID_045E&PID_0B12&IG_00\NOT&PRESENT", () => true, TreeWalk()));
        }

        /// <summary>A persona is a USB device, so each of its nodes carries
        /// the VID_xxxx&amp;PID_yyyy pair. A node under the usbip-win2 host
        /// controller without one, such as a Bluetooth pad behind a radio
        /// attached that way, is no persona whatever is in scope. A node
        /// that is not present is judged by its own id alone.</summary>
        [Fact]
        public void ANodeWithNoUsbIdsIsNoPersona()
        {
            HidHideController.PersonaScopeSeam = (_, _) => true;
            HidHideController.OwnerChainSeam = _ => OverUsbip("VID_F00D&PID_0CE6", stamped: true, serviceReads: true);
            Assert.False(HidHideController.IsHidMaestroDeviceInstance(
                @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002F00D_PID&0CE6\9&1&0&0000"));
            Assert.True(HidHideController.IsHidMaestroDeviceInstance(@"HID\VID_F00D&PID_0CE6&MI_03\8&1&0&0000"));

            HidHideController.OwnerChainSeam = _ => null;
            Assert.False(HidHideController.IsHidMaestroDeviceInstance(@"HID\VID_F00D&PID_0CE6&MI_03\8&1&0&0000"));
            Assert.True(HidHideController.IsHidMaestroDeviceInstance(@"ROOT\VID_045E&PID_028E&IG_00\0000"));
        }

        /// <summary>For a node under the usbip-win2 host controller with no
        /// persona serial above it, the id scope decides, and it is the one
        /// Step 1 asks of such a device: the personas this process makes
        /// (InputManager.ChecksUsbipAncestry). With none of a model live,
        /// the node is not HIDMaestro's. From the moment PadForge is about
        /// to make one, it is.</summary>
        [Fact]
        public void ThePersonaScopeIsTheOneStep1Uses()
        {
            const string pad = @"HID\VID_F00D&PID_0CE7&MI_03\8&1&0&0000";
            HidHideController.OwnerChainSeam = _ => OverUsbip("VID_F00D&PID_0CE7", stamped: true, serviceReads: true);
            var owner = new object();
            try
            {
                Assert.False(HidHideController.IsHidMaestroDeviceInstance(pad));
                InputManager.NotePersonaLive(owner, 0xF00D, 0x0CE7);
                Assert.True(HidHideController.IsHidMaestroDeviceInstance(pad));
            }
            finally
            {
                InputManager.NotePersonaRetired(owner);
            }
        }
    }
}
