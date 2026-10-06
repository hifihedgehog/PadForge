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
    /// Refused, PadForge takes the hide back, says so and stops hiding.
    /// Allowed, the hide stays and the probe runs once. With HidHide's
    /// inverse application cloak on, PadForge keeps itself off the list and
    /// hides nothing.</para>
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
                        return Read(Blacklist, outBuf);
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

        public HidHideSelfAccessTests()
        {
            HidHideController.ResetManagedForTests();
            HidHideController.IoSeam = _driver.Io;
            HidHideController.InterfaceSeam = id =>
                string.Equals(id, RemoteId, StringComparison.OrdinalIgnoreCase) ? _interface : null;
            HidHideController.OpenSeam = path => { _opened.Add(path); return _openAnswer; };
            HidHideController.SerialReader = _ => null;

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
        }

        public void Dispose()
        {
            _svc.Dispose();
            HidHideController.IoSeam = null;
            HidHideController.InterfaceSeam = null;
            HidHideController.OpenSeam = null;
            HidHideController.SerialReader = null;
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
        /// takes the hide back, says why, and hides nothing again in this
        /// process.</summary>
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
        /// reaches the log, with PadForge's native path and the fact that the
        /// driver's list held it.</summary>
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
            Assert.EndsWith($"native={native} listed=yes", line);
        }

        /// <summary>A whitelist the driver will not write or read is logged,
        /// and the refusal that follows says what the list held. No entry for
        /// PadForge means the write never landed, a different fault from a
        /// driver that does not know this process by its path.</summary>
        [Theory]
        [InlineData(false, "no")]
        [InlineData(true, "unreadable")]
        public void AWhitelistTheDriverWillNotTakeIsLoggedAndTheRefusalSaysWhatItHeld(bool unreadable, string listed)
        {
            _driver.RefuseWhitelistWrites = !unreadable;
            _driver.RefuseWhitelistReads = unreadable;
            _openAnswer = (false, ERROR_ACCESS_DENIED);
            string mark = Mark();

            _svc.ApplyDeviceHiding();

            Assert.Single(LogLinesAfter(mark, "HIDHIDE whitelist could not be read or written"));
            Assert.EndsWith(" listed=" + listed, Assert.Single(LogLinesAfter(mark, "HIDHIDE REFUSED")));
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
            int write = svc.IndexOf("bool synced = HidHideController.SyncManagedDevices(desiredIds, out var added, out var removed);", apply, StringComparison.Ordinal);
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
    }
}
