using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Keeps the test process away from this PC's HidHide and from its USB
    /// ports for its whole life.
    /// </summary>
    /// <remarks>
    /// The hiding tests run PadForge's own apply against a fake driver that
    /// a seam puts in the control device's place, and they record a port
    /// cycle where the app would make one. A call that found no seam went
    /// on to the real thing: a test that never set one, work that outlived
    /// its test's teardown, a mutant of the app that dropped the check
    /// which sends the call to its seam. On a PC with HidHide installed that
    /// opens its control device, which takes one client at a time, and can
    /// write the lists that decide which devices every program sees. A
    /// release with no seam for its cycle disconnects a controller that may
    /// be in use. With this set, a call no seam stands in for changes
    /// nothing and answers as a failed call would.
    ///
    /// <para>With the environment variable PADFORGE_OFFLIMITS_LOG naming a
    /// file, each call so refused is added to that file with the stack it
    /// came from. A test run or a mutation run started that way shows which
    /// calls it would have sent to the PC.</para>
    /// </remarks>
    internal static class HidHideOffLimits
    {
        internal const string LogVariable = "PADFORGE_OFFLIMITS_LOG";
        private static readonly object s_logGate = new();

#pragma warning disable CA2255 // Only the test host loads this assembly.
        [ModuleInitializer]
        internal static void Set()
        {
            HidHideController.SeamsOnlyForTest = true;
            string log = Environment.GetEnvironmentVariable(LogVariable);
            if (!string.IsNullOrEmpty(log))
                HidHideController.OffLimitsRefused = call => Record(log, call);
        }
#pragma warning restore CA2255

        private static void Record(string log, string call)
        {
            try
            {
                lock (s_logGate)
                    File.AppendAllText(log, "CALL " + call + "\n" + Environment.StackTrace + "\n-----\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Both sides of <see cref="HidHideOffLimits"/>: in this process every
    /// call that would reach the PC refuses when no seam stands in for it,
    /// and nothing in the app sets the flag that makes it refuse.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class HidHideOffLimitsTests : IDisposable
    {
        private const uint IOCTL_GET_BLACKLIST = 0x80016008;
        private const uint IOCTL_SET_BLACKLIST = 0x8001600C;
        private const int ERROR_FILE_NOT_FOUND = 2;

        // Ids no real device carries, so a call that got through both
        // checks would still find nothing to change.
        private const string NoSuchPath =
            @"\\?\HID#VID_F00D&PID_0FF1#7&0ff&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private static readonly string NoSuchId = HidHideController.DevicePathToInstanceId(NoSuchPath);
        private const string NoSuchUsb = @"USB\VID_F00D&PID_0FF1\OFFLIMITS";
        private const string NoSuchProgram = @"\Device\HarddiskVolume0\off-limits\none.exe";

        // The calls the flag refused while a test of this class ran. Other
        // classes run beside this one and are refused too, so a test looks
        // for its own calls among them and counts none.
        private readonly List<string> _refused = new();
        private readonly Action<string> _before = HidHideController.OffLimitsRefused;

        public HidHideOffLimitsTests()
        {
            // Every test here makes the calls that would reach this PC, with
            // no seam in place, and is safe only while the flag refuses them.
            // Without the flag no test of this class runs.
            if (!HidHideController.SeamsOnlyForTest)
                throw new InvalidOperationException("the test host did not set HidHideController.SeamsOnlyForTest, so these tests would reach this PC's HidHide");
            // Nothing stands in for the control device in these tests.
            HidHideController.IoSeam = null;
            HidHideController.ControlOpenSeam = null;
            HidHideController.ResetManagedForTests();
            HidHideController.OffLimitsRefused = call =>
            {
                lock (_refused) _refused.Add(call);
                _before?.Invoke(call);
            };
        }

        public void Dispose()
        {
            HidHideController.OffLimitsRefused = _before;
            HidHideController.IoSeam = null;
            HidHideController.ResetManagedForTests();
        }

        private string[] Refused()
        {
            lock (_refused) return _refused.ToArray();
        }

        [Fact]
        public void TheTestHostSetsTheFlag()
        {
            Assert.True(HidHideController.SeamsOnlyForTest);
        }

        /// <summary>The announcement maps no image here. The driver of the
        /// PC the tests run on would hear the test host's image, and a
        /// mutant of the app could map another.</summary>
        [Fact]
        public void AnAnnouncementMapsNothingWithNoSeam()
        {
            HidHideController.AnnounceSeam = null;

            Assert.False(HidHideController.AnnounceThisProcess());

            Assert.Contains("announce", Refused());
        }

        /// <summary>The control device does not open, and the error is the
        /// one a PC with no HidHide gives. The release reads that as a
        /// driver that is not there, so its batch ends.</summary>
        [Fact]
        public void TheControlDeviceDoesNotOpenWithNoSeam()
        {
            Assert.False(HidHideController.IsAvailable());
            Assert.False(HidHideController.TryProbe(out int error));
            Assert.Equal(ERROR_FILE_NOT_FOUND, error);
            Assert.False(HidHideArm64Installer.ControlDeviceAnswers(false, error));
            Assert.StartsWith("hidhide=unavailable ", HidHideController.DescribeReach("none.exe"));

            Assert.True(Refused().Count(call => call == "open") >= 3);
            Assert.DoesNotContain(Refused(), call => call.StartsWith("past its refusal", StringComparison.Ordinal));
        }

        /// <summary>No read of a list or a flag is sent. Each answers as it
        /// does when the driver could not be read.</summary>
        [Fact]
        public void NoReadReachesTheDriverWithNoSeam()
        {
            Assert.Null(HidHideController.GetBlacklist());
            Assert.Null(HidHideController.GetWhitelist());
            Assert.False(HidHideController.TryGetActive(out bool active));
            Assert.False(active);
            Assert.False(HidHideController.GetActive());
            Assert.False(HidHideController.TryGetInverse(out bool inverse));
            Assert.False(inverse);
            Assert.Null(HidHideController.MissingFromBlacklist(new[] { NoSuchId }));
            Assert.Equal(-1, HidHideController.AdoptExistingAsManaged());

            foreach (string read in new[] { "ioctl 0x80016008", "ioctl 0x80016000", "ioctl 0x80016010", "ioctl 0x80016018" })
                Assert.Contains(read, Refused());
            Assert.DoesNotContain(Refused(), call => call.StartsWith("past its refusal", StringComparison.Ordinal));
        }

        /// <summary>No write of a list or a flag is sent. Each answers as a
        /// refused write does, and the set of entries PadForge counts as
        /// its own stays as it was.</summary>
        [Fact]
        public void NoWriteReachesTheDriverWithNoSeam()
        {
            Assert.False(HidHideController.SetBlacklist(new List<string> { NoSuchId }));
            Assert.False(HidHideController.SetWhitelist(new List<string> { NoSuchProgram }));
            Assert.False(HidHideController.RemoveWhitelistEntries(new[] { NoSuchProgram }));
            Assert.False(HidHideController.SyncManagedDevices(new HashSet<string> { NoSuchId }, out var added, out var removed));
            Assert.Empty(added);
            Assert.Empty(removed);
            Assert.False(HidHideController.IsManagedDevice(NoSuchId));
            HidHideController.SetActive(true);
            HidHideController.ClearAll();
            HidHideController.RemoveManagedDevices();

            foreach (string write in new[] { "ioctl 0x8001600C", "ioctl 0x80016004", "ioctl 0x80016014" })
                Assert.Contains(write, Refused());
            Assert.DoesNotContain(Refused(), call => call.StartsWith("past its refusal", StringComparison.Ordinal));

            // An entry a seam's driver took stays PadForge's own once the
            // seam is gone: the removal reads no list and writes none.
            var list = new List<string>();
            HidHideController.IoSeam = (ioctl, inBuf, outBuf) => ADriverWithOneList(list, ioctl, inBuf, outBuf);
            Assert.True(HidHideController.SyncManagedDevices(new HashSet<string> { NoSuchId }, out _, out _));
            Assert.Equal(new[] { NoSuchId }, list);
            HidHideController.IoSeam = null;
            HidHideController.RemoveManagedDevices();
            HidHideController.ClearAll();
            Assert.True(HidHideController.IsManagedDevice(NoSuchId));
            Assert.Equal(new[] { NoSuchId }, list);
        }

        /// <summary>A driver that holds one list, with HidHide's two-call
        /// read: a probe for the size, then the read.</summary>
        private static (bool ok, int bytes) ADriverWithOneList(List<string> list, uint ioctl, byte[] inBuf, byte[] outBuf)
        {
            if (ioctl == IOCTL_SET_BLACKLIST)
            {
                list.Clear();
                list.AddRange(System.Text.Encoding.Unicode.GetString(inBuf ?? Array.Empty<byte>())
                    .Split('\0').Where(entry => entry.Length > 0));
                return (true, 0);
            }
            if (ioctl != IOCTL_GET_BLACKLIST) return (false, 0);
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(string.Concat(list.Select(entry => entry + "\0")) + "\0");
            if (outBuf == null || outBuf.Length == 0) return (true, bytes.Length);
            if (outBuf.Length < bytes.Length) return (false, 0);
            Array.Copy(bytes, outBuf, bytes.Length);
            return (true, bytes.Length);
        }

        /// <summary>The release cycles no port. Its batch reaches the cycle
        /// with a USB device to cycle and no seam for the cycle, and the
        /// cycle fails the way a hub's refusal does.</summary>
        [Fact]
        public void NoPortIsCycledWithNoSeam()
        {
            var lines = new List<string>();
            using var release = new HiddenControllerRelease
            {
                Resolve = (_, _, _) => (NoSuchUsb, null),
                ReadBlacklist = () => (new[] { NoSuchId }, true),
                Log = line => { lock (lines) lines.Add(line); },
                Schedule = (_, _) => { },
            };

            release.Release(new[] { NoSuchId });
            Assert.True(BatchesDone(release));

            lock (lines)
            {
                Assert.Equal(
                    $"HIDHIDE release failed {NoSuchUsb}: InvalidOperationException: no port is cycled in a test host with no seam for the cycle",
                    Assert.Single(lines));
            }
            Assert.Empty(release.TakeCycledIds());
            Assert.Contains("cycle " + NoSuchUsb, Refused());
            Assert.DoesNotContain(Refused(), call => call.StartsWith("past its refusal", StringComparison.Ordinal));
        }

        private static bool BatchesDone(HiddenControllerRelease release)
            => release.Pending.Wait(TimeSpan.FromSeconds(30));

        /// <summary>PadForge's own apply, purge and stand-down with no seam:
        /// each finds no HidHide, says so where it hides on request, and
        /// leaves no entry it counts as its own.</summary>
        [Fact]
        public void TheApplyWithNoSeamHidesNothing()
        {
            var saved = SettingsManager.UserDevices;
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = "A pad no PC has",
                VendorId = 0xF00D,
                ProdId = 0x0FF1,
                DevicePath = NoSuchPath,
                HidHideEnabled = true,
                IsOnline = true,
            });
            var vm = new MainViewModel();
            vm.Settings.EnableInputHiding = true;
            var svc = new InputService(vm);
            try
            {
                string mark = "HidHideOffLimitsTests mark " + Guid.NewGuid().ToString("N");
                SdlDiagLog.WriteLine(mark);

                svc.ApplyDeviceHiding();
                svc.PurgeStaleHidHideCloaks();
                svc.RemoveDeviceHiding();

                var lines = SdlDiagLog.Snapshot().Split('\n').Select(l => l.TrimEnd()).ToList();
                int at = lines.FindIndex(l => l.EndsWith(mark, StringComparison.Ordinal));
                Assert.True(at >= 0, "the mark rolled out of the diagnostics ring");
                Assert.Contains(lines.Skip(at + 1), l => l.EndsWith(
                    "HIDHIDE UNAVAILABLE: hiding requested for 1 device(s) but the control device did not open (err=2). Nothing is hidden.",
                    StringComparison.Ordinal));
                Assert.False(HidHideController.IsManagedDevice(NoSuchId));
                Assert.DoesNotContain(Refused(), call => call.StartsWith("past its refusal", StringComparison.Ordinal));
            }
            finally
            {
                svc.Dispose();
                BatchesDone(svc.ControllerRelease);
                SettingsManager.UserDevices = saved;
            }
        }

        /// <summary>The file a run asks for, when it asks: one record per
        /// refused call, with the name the app gave it.</summary>
        [Fact]
        public void ARefusedCallIsWrittenToTheFileARunAsksFor()
        {
            string log = Environment.GetEnvironmentVariable(HidHideOffLimits.LogVariable);
            if (string.IsNullOrEmpty(log))
            {
                // Not asked for in this run: the app's seam is this class's own.
                Assert.Null(_before);
                return;
            }
            long before = File.Exists(log) ? new FileInfo(log).Length : 0;
            Assert.False(HidHideController.IsAvailable());
            // Other classes are refused beside this one and write to the
            // same file, so it is read shared, from where it ended before.
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(before, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            Assert.Contains("CALL open\n", reader.ReadToEnd());
        }

        private static string SourceOf(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }

        /// <summary>The second check. Each call that reaches the PC has the
        /// throwing check as the line before it, so a change that loses the
        /// refusal above it still cannot get to the device: the control
        /// device's two opens, the one IOCTL call every list and flag goes
        /// through, and the one hub port cycle. Read from the source, since
        /// no test may take the first check away to watch the second.</summary>
        [Fact]
        public void EveryCallThatReachesThePcHasTheThrowingCheckBeforeIt()
        {
            string ctl = File.ReadAllText(SourceOf("PadForge.App", "Common", "HidHideController.cs"));
            Assert.Equal(2, Regex.Matches(ctl, @"CreateFileW\(\s*DevicePath\b").Count);
            Assert.Equal(2, Regex.Matches(ctl,
                @"ThrowIfOffLimits\(""Opening HidHide's control device""\);\s*var handle = CreateFileW\(\s*DevicePath\b").Count);
            // The declaration and the one call.
            Assert.Equal(2, Regex.Matches(ctl, @"\bDeviceIoControl\(").Count);
            Assert.Single(Regex.Matches(ctl,
                @"ThrowIfOffLimits\(""An IOCTL to HidHide's control device""\);\s*return DeviceIoControl\(handle, ioctl,"));
            Assert.Single(Regex.Matches(ctl,
                @"internal static void ThrowIfOffLimits\(string call\)\s*\{\s*if \(SeamsOnlyForTest\)\s*\{\s*OffLimitsRefused\?\.Invoke\(""past its refusal: "" \+ call\);\s*throw new InvalidOperationException\("));

            string release = File.ReadAllText(SourceOf("PadForge.App", "Services", "HiddenControllerRelease.cs"));
            Assert.Single(Regex.Matches(release, @"\.CyclePort\(\)"));
            Assert.Single(Regex.Matches(release,
                @"HidHideController\.ThrowIfOffLimits\(""Cycling a USB hub port""\);\s*PnPDevice\.GetDeviceByInstanceId\(usb, DeviceLocationFlags\.Normal\)\.ToUsbPnPDevice\(\)\.CyclePort\(\);"));

            // The apply itself names no device: every call of its hiding
            // part goes through the two files above.
            foreach (string name in new[] { "InputService.cs", "InputService.Tablets.cs" })
            {
                string svc = File.ReadAllText(SourceOf("PadForge.App", "Services", name));
                Assert.DoesNotContain(@"\\.\HidHide", svc);
                Assert.DoesNotContain(".CyclePort()", svc);
                Assert.DoesNotContain("DeviceIoControl(", svc);
            }
        }

        /// <summary>The other side: the app never sets the flag, and never
        /// the seam that is told of a refusal. Outside comments,
        /// HidHideController declares each once with no value, reads the
        /// flag in an if and calls the seam, the release does the same, and
        /// no other file of the app or the engine names either, not even by
        /// a string a reflection call could take.</summary>
        [Fact]
        public void NothingInTheAppSetsTheFlag()
        {
            var declared = new List<string>();
            var other = new List<string>();
            foreach (string project in new[] { "PadForge.App", "PadForge.Engine" })
            {
                string root = SourceOf(project);
                foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(root, file);
                    if (relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;
                    bool controller = string.Equals(Path.GetFileName(file), "HidHideController.cs", StringComparison.OrdinalIgnoreCase);
                    int number = 0;
                    foreach (string raw in File.ReadLines(file))
                    {
                        number++;
                        string line = raw.Trim();
                        if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                        bool mine = controller
                            ? Regex.IsMatch(line, @"\b(SeamsOnlyForTest|OffLimitsRefused)\b")
                            : Regex.IsMatch(line, @"\bHidHideController\.(SeamsOnlyForTest|OffLimitsRefused)\b")
                              || Regex.IsMatch(line, @"""(SeamsOnlyForTest|OffLimitsRefused)""")
                              || line.Contains("using static PadForge.Common.HidHideController", StringComparison.Ordinal);
                        if (!mine) continue;
                        if (controller && (line == "internal static bool SeamsOnlyForTest;"
                                           || line == "internal static Action<string> OffLimitsRefused;"))
                            declared.Add(line);
                        else if (line != "if (SeamsOnlyForTest)"
                                 && line != "if (HidHideController.SeamsOnlyForTest)"
                                 && !Regex.IsMatch(line, @"^(HidHideController\.)?OffLimitsRefused\?\.Invoke\(.*\);$"))
                            other.Add($"{project}/{relative}:{number}: {line}");
                    }
                }
            }
            Assert.Equal(
                new[] { "internal static Action<string> OffLimitsRefused;", "internal static bool SeamsOnlyForTest;" },
                declared.OrderBy(line => line, StringComparer.Ordinal));
            Assert.Empty(other);
        }
    }
}
