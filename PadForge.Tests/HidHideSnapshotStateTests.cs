using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #541: a DolphinBar report said PadForge's virtual controllers vanished
    /// from XInput and Game Controllers while the bar was plugged in, and the
    /// log could show neither HidHide entries PadForge did not add nor what
    /// Windows listed for each virtual controller. These pin the split of
    /// HidHide's list into PadForge's entries and the rest, the state lines
    /// Save Snapshot writes, and the sync line that names the rest.
    ///
    /// <para>The fake driver stands in for HidHide's control device through
    /// HidHideController.IoSeam, as in HidHideDiagnosticsTests, so the class
    /// runs in that collection and never beside it.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class HidHideSnapshotStateTests : IDisposable
    {
        private const uint IOCTL_GET_BLACKLIST = 0x80016008;
        private const uint IOCTL_SET_BLACKLIST = 0x8001600C;
        private const uint IOCTL_GET_ACTIVE = 0x80016010;
        private const uint IOCTL_GET_WLINVERSE = 0x80016018;

        // A VID and PID no real device carries, so nothing on the bench
        // matches, and HIDMaestro ids in the shape PadForge's virtual
        // Xbox Series controllers take.
        private const string Ours = @"HID\VID_F00D&PID_0306&MI_00\7&170fdced&0&0000";
        private const string Theirs = @"HID\VID_F00D&PID_0B13&IG_00\9&35c399e5&0&0000";
        private const string VirtualA = @"HID\HIDMAESTRO_VID_045E_PID_0B13&IG_00\1&D3DD7A1F&0&0000";
        private const string VirtualB = @"HID\HIDMAESTRO_VID_045E_PID_0B13&IG_00\1&D3DD7A1F&0&0001";
        private const string Real = @"HID\VID_F00D&PID_C52B&MI_02&COL01\8&1&0&0000";
        private const string XusbA = @"\\?\HID#HIDMAESTRO_VID_045E_PID_0B13&IG_00#1&d3dd7a1f&0&0000#{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}";

        private sealed class FakeDriver
        {
            public readonly List<string> List = new();
            public bool Active = true;
            public bool Inverse;

            public (bool ok, int bytes) Io(uint ioctl, byte[] inBuf, byte[] outBuf)
            {
                switch (ioctl)
                {
                    case IOCTL_GET_BLACKLIST:
                    {
                        int chars = 1;
                        foreach (var s in List) chars += s.Length + 1;
                        int needed = chars * 2;
                        if (outBuf == null || outBuf.Length == 0) return (true, needed);
                        if (outBuf.Length < needed) return (false, 0);
                        var sb = new StringBuilder();
                        foreach (var s in List) { sb.Append(s); sb.Append('\0'); }
                        sb.Append('\0');
                        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
                        Array.Copy(bytes, outBuf, bytes.Length);
                        return (true, needed);
                    }
                    case IOCTL_SET_BLACKLIST:
                    {
                        List.Clear();
                        if (inBuf != null && inBuf.Length > 0)
                            foreach (var e in Encoding.Unicode.GetString(inBuf).Split('\0'))
                                if (e.Length > 0) List.Add(e);
                        return (true, inBuf?.Length ?? 0);
                    }
                    case IOCTL_GET_ACTIVE:
                        outBuf[0] = Active ? (byte)1 : (byte)0;
                        return (true, 1);
                    case IOCTL_GET_WLINVERSE:
                        outBuf[0] = Inverse ? (byte)1 : (byte)0;
                        return (true, 1);
                    default:
                        return (false, 0);
                }
            }
        }

        private readonly FakeDriver _fake = new();

        public HidHideSnapshotStateTests()
        {
            HidHideController.ResetManagedForTests();
            HidHideController.IoSeam = _fake.Io;
            // A node that does not name HIDMaestro is judged by its walk, and
            // no walk here leads to HIDMaestro.
            HidHideController.OwnerChainSeam = _ => null;
            HidHideController.PresentHidNodesSeam = () => new[] { VirtualA, Real, VirtualB };
            HidHideController.XusbInterfaceSeam = id => string.Equals(id, VirtualA, StringComparison.OrdinalIgnoreCase);
            HidHideController.XusbInterfaceListSeam = () => new[] { XusbA };
            HidHideController.ProblemSeam = id => string.Equals(id, VirtualB, StringComparison.OrdinalIgnoreCase) ? 10 : 0;
        }

        public void Dispose()
        {
            HidHideController.IoSeam = null;
            HidHideController.ControlOpenSeam = null;
            HidHideController.OwnerChainSeam = null;
            HidHideController.PresentHidNodesSeam = null;
            HidHideController.XusbInterfaceSeam = null;
            HidHideController.XusbInterfaceListSeam = null;
            HidHideController.ProblemSeam = null;
            HidHideController.ResetManagedForTests();
            DiagnosticsLogControl.StateSeam = null;
        }

        [Fact]
        public void OthersIn_KeepsWhatPadForgeDidNotAdd_InTheListsOrder()
        {
            var mine = new HashSet<string>(new[] { Ours.ToLowerInvariant() }, StringComparer.OrdinalIgnoreCase);
            var others = HidHideController.OthersIn(new[] { Theirs, Ours, "", VirtualA, null }, mine.Contains);
            Assert.Equal(new[] { Theirs, VirtualA }, others);
            Assert.Empty(HidHideController.OthersIn(null, mine.Contains));
        }

        [Fact]
        public void SnapshotState_CountsPadForgesEntriesApart_AndNamesEachVirtualController()
        {
            // Another program listed a pad and one of PadForge's virtual
            // controllers. PadForge's sync then adds its own entry, which
            // makes that one PadForge's.
            _fake.List.Add(Theirs);
            _fake.List.Add(VirtualA);
            Assert.True(HidHideController.SyncManagedDevices(
                new HashSet<string>(new[] { Ours }, StringComparer.OrdinalIgnoreCase)));

            var lines = HidHideController.DescribeSnapshotState();

            string hidhide = Assert.Single(lines, l => l.StartsWith("STATE hidhide active=", StringComparison.Ordinal));
            Assert.StartsWith("STATE hidhide active=true inverse=false listed=3 padforge=1 others=2 [", hidhide);
            Assert.Contains(Theirs, hidhide);
            Assert.Contains(VirtualA, hidhide);
            Assert.DoesNotContain(Ours, hidhide);
            Assert.Contains($"STATE hidhide lists PadForge virtual controllers: [{VirtualA}]", lines);
            Assert.Contains($"STATE xinput interfaces n=1 [{XusbA}]", lines);
            Assert.Contains($"STATE virtual {VirtualA} problem=none hidden=yes xinput=yes", lines);
            Assert.Contains($"STATE virtual {VirtualB} problem=10 hidden=no xinput=no", lines);
            // A real device's node is not a virtual controller's.
            Assert.DoesNotContain(lines, l => l.Contains(Real));
        }

        [Fact]
        public void SnapshotState_WithoutHidHide_SaysSo_AndStillDescribesWindows()
        {
            HidHideController.IoSeam = null;
            HidHideController.ControlOpenSeam = () => 2; // ERROR_FILE_NOT_FOUND
            HidHideController.XusbInterfaceListSeam = () => Array.Empty<string>();

            var lines = HidHideController.DescribeSnapshotState();

            Assert.Equal("STATE hidhide unavailable", lines[0]);
            Assert.Contains("STATE xinput interfaces n=0", lines);
            Assert.Contains($"STATE virtual {VirtualA} problem=none hidden=unknown xinput=yes", lines);
        }

        [Fact]
        public void SnapshotState_WithNoVirtualController_SaysNone_AndReadsBothFlags()
        {
            HidHideController.PresentHidNodesSeam = () => new[] { Real };
            _fake.Active = false;
            _fake.Inverse = true;
            var lines = HidHideController.DescribeSnapshotState();
            Assert.Contains("STATE virtual none", lines);
            Assert.Contains("STATE hidhide active=false inverse=true listed=0 padforge=0 others=0", lines);
        }

        [Fact]
        public void SaveSnapshot_EndsWithTheStateLines_AndAFailedReadLeavesItsOwnLine()
        {
            DiagnosticsLogControl.StateSeam = () => new[] { "STATE hidhide test-541-one", "STATE virtual test-541-two" };
            string path = DiagnosticsLogControl.SaveSnapshot();
            try
            {
                string[] text = File.ReadAllLines(path);
                int one = Array.FindLastIndex(text, l => l.EndsWith("STATE hidhide test-541-one", StringComparison.Ordinal));
                int two = Array.FindLastIndex(text, l => l.EndsWith("STATE virtual test-541-two", StringComparison.Ordinal));
                // Other tests write to the same history, so the two lines
                // need not sit side by side, only in order.
                Assert.True(one >= 0 && two > one, $"one={one} two={two}");
            }
            finally
            {
                File.Delete(path);
            }

            DiagnosticsLogControl.StateSeam = () => throw new InvalidOperationException("no read");
            Assert.Equal(new[] { "STATE err:InvalidOperationException" }, DiagnosticsLogControl.SnapshotStateLines());
        }

        /// <summary>The sync names the entries PadForge did not add, flags
        /// any that is a PadForge virtual controller, and prints the block
        /// when that set changes, through the one-time-line flag the print
        /// test already honors.</summary>
        [Fact]
        public void Sync_NamesTheOthers_WhenTheyChange()
        {
            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = svc.IndexOf("// #541: the entries in HidHide's list that PadForge did not", StringComparison.Ordinal);
            Assert.True(at > 0);
            int end = svc.IndexOf("long tick = Environment.TickCount64;", at, StringComparison.Ordinal);
            Assert.True(end > at);
            string block = svc.Substring(at, end - at);
            Assert.Contains("var listedNow = HidHideController.GetBlacklist();", block);
            Assert.Contains("HidHideController.OthersIn(listedNow,", block);
            Assert.Contains("id => desiredIds.Contains(id) || HidHideController.IsManagedDevice(id));", block);
            Assert.Contains("HIDHIDE others n={others.Count}", block);
            Assert.Contains("others.Where(HidHideController.IsHidMaestroDeviceInstance)", block);
            Assert.Contains("oneTimeLine = true;", block);
            Assert.Contains("_lastHidHideOthers = new HashSet<string>(others, StringComparer.OrdinalIgnoreCase);", block);
            Assert.Contains("if (desiredMoved || refusedNow || inverseMoved || oneTimeLine", svc.Substring(end));
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
