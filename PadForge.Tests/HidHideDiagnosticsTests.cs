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
    /// HidHide observability (#391, discussion #388). ApplyDeviceHiding
    /// logged nothing before this, so a "the physical was not hidden"
    /// report could not be judged from a trace. These pin the read-back
    /// set difference and the source contracts that make the hiding path
    /// readable: the availability probe with its error, the per-device
    /// resolution and expansion, the sync diff with activation, and the
    /// read-back of the driver's own list.
    ///
    /// <para>The fake driver below stands in for the control device
    /// through HidHideController.IoSeam and mirrors the driver's own
    /// contract (HidHide Logic.c OnControlDeviceIoGetBlacklist and
    /// Config.c HidHideCollectionToMultiString): a zero-length probe
    /// answers with the byte count needed, a read into a buffer smaller
    /// than that fails, a read into a buffer over 32767 characters fails
    /// the way RtlStringCchCopyUnicodeStringEx's validator does, and a
    /// SET replaces the whole list.</para>
    /// </summary>
    public class HidHideDiagnosticsTests
    {
        private const uint IOCTL_GET_BLACKLIST = 0x80016008;
        private const uint IOCTL_SET_BLACKLIST = 0x8001600C;
        private const uint IOCTL_GET_ACTIVE = 0x80016010;
        private const uint IOCTL_SET_ACTIVE = 0x80016014;

        private sealed class FakeDriver
        {
            public readonly List<string> List = new();
            public readonly List<List<string>> Writes = new();
            public bool RefuseSet;
            public bool Active;

            /// <summary>The driver's needed count, in its own units: each
            /// UNICODE_STRING's Length (bytes) plus one, plus one, then
            /// times sizeof(WCHAR) for the completion information.</summary>
            public int NeededBytes()
            {
                int chars = 0;
                foreach (var s in List) chars += s.Length * 2 + 1;
                chars += 1;
                return chars * 2;
            }

            public (bool ok, int bytes) Io(uint ioctl, byte[] inBuf, byte[] outBuf)
            {
                switch (ioctl)
                {
                    case IOCTL_GET_BLACKLIST:
                    {
                        int needed = NeededBytes();
                        if (outBuf == null || outBuf.Length == 0) return (true, needed);
                        int cch = outBuf.Length / 2;
                        if (cch < needed / 2) return (false, 0);      // STATUS_BUFFER_TOO_SMALL
                        if (cch > 32767) return (false, 0);           // STATUS_INVALID_PARAMETER from strsafe
                        var sb = new StringBuilder();
                        foreach (var s in List) { sb.Append(s); sb.Append('\0'); }
                        sb.Append('\0');
                        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
                        Array.Copy(bytes, outBuf, bytes.Length);
                        return (true, needed);
                    }
                    case IOCTL_SET_BLACKLIST:
                    {
                        var parsed = new List<string>();
                        if (inBuf != null && inBuf.Length > 0)
                            foreach (var e in Encoding.Unicode.GetString(inBuf).Split('\0'))
                                if (e.Length > 0) parsed.Add(e);
                        Writes.Add(parsed);
                        if (RefuseSet) return (false, 0);
                        List.Clear();
                        List.AddRange(parsed);
                        return (true, inBuf?.Length ?? 0);
                    }
                    case IOCTL_GET_ACTIVE:
                        outBuf[0] = Active ? (byte)1 : (byte)0;
                        return (true, 1);
                    case IOCTL_SET_ACTIVE:
                        Active = inBuf[0] != 0;
                        return (true, 0);
                    default:
                        return (false, 0);
                }
            }
        }

        private static IDisposable Install(FakeDriver fake)
        {
            HidHideController.ResetManagedForTests();
            HidHideController.IoSeam = fake.Io;
            return new Uninstall();
        }

        private sealed class Uninstall : IDisposable
        {
            public void Dispose()
            {
                HidHideController.IoSeam = null;
                HidHideController.ResetManagedForTests();
            }
        }

        /// <summary>The present-node sweep (#391) widens a record's hide
        /// list only when it is the sole PRESENT record of its VID/PID, so
        /// two distinct pads of one product never hide each other. A
        /// record counts when it is the one being applied, is online, or
        /// is offline with a path or cached id the presence probe finds.
        /// An offline record whose path resolves to nothing is a stale
        /// registry entry and must not switch the sweep off.</summary>
        [Fact]
        public void SiblingSweep_OnlyForTheSoleRecordOfItsProduct()
        {
            var ds = new PadForge.Engine.Data.UserDevice { VendorId = 0x054C, ProdId = 0x0CE6 };
            var xbox = new PadForge.Engine.Data.UserDevice { VendorId = 0x045E, ProdId = 0x0B13 };
            var ds2 = new PadForge.Engine.Data.UserDevice { VendorId = 0x054C, ProdId = 0x0CE6, IsOnline = true };
            Func<string, bool> nothingPresent = _ => false;

            Assert.True(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds, xbox }, ds, out int same, nothingPresent));
            Assert.Equal(1, same);
            Assert.False(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds, ds2, xbox }, ds, out same, nothingPresent));
            Assert.Equal(2, same);
            Assert.True(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds, null, xbox }, ds, out _, nothingPresent));
            Assert.False(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds }, null, out _, nothingPresent));
            Assert.False(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds }, new PadForge.Engine.Data.UserDevice(), out _, nothingPresent));

            // THE BUG: a second, OFFLINE record of the product (the pad
            // paired earlier under another serial) whose persisted path
            // resolves to no present devnode. The old count read two and
            // the live pad's other transport was never hidden.
            var stale = new PadForge.Engine.Data.UserDevice
            {
                VendorId = 0x054C, ProdId = 0x0CE6, IsOnline = false,
                DevicePath = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#9&deadbeef&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            };
            Assert.True(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds, stale, xbox }, ds, out same, nothingPresent));
            Assert.Equal(1, same);

            // The same offline record with a path the probe DOES find is a
            // second pad that is plugged in, and the sweep stays off.
            string staleId = HidHideController.DevicePathToInstanceId(stale.DevicePath);
            Func<string, bool> stalePresent = id => string.Equals(id, staleId, StringComparison.OrdinalIgnoreCase);
            Assert.False(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds, stale, xbox }, ds, out same, stalePresent));
            Assert.Equal(2, same);

            // A cached instance id counts the same way as the path.
            var cachedOnly = new PadForge.Engine.Data.UserDevice { VendorId = 0x054C, ProdId = 0x0CE6, DevicePath = "XInput#1" };
            cachedOnly.HidHideInstanceIds.Add(@"HID\VID_054C&PID_0CE6\7&1");
            Func<string, bool> cachedPresent = id => id.StartsWith(@"HID\VID_054C&PID_0CE6\7&1", StringComparison.OrdinalIgnoreCase);
            Assert.False(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds, cachedOnly }, ds, out _, cachedPresent));
            Assert.True(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds, cachedOnly }, ds, out _, nothingPresent));

            // The two-argument form is the production default (cfgmgr32).
            Assert.True(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds, xbox }, ds));
        }

        [Fact]
        public void ComputeMissing_IsACaseInsensitiveSetDifference()
        {
            var desired = new[] { @"HID\VID_054C&PID_0CE6&MI_03\8&1", @"USB\VID_054C&PID_0CE6&MI_03\7&1" };
            var present = new[] { @"hid\vid_054c&pid_0ce6&mi_03\8&1" };
            var missing = HidHideController.ComputeMissing(desired, present);
            Assert.Single(missing);
            Assert.Equal(desired[1], missing[0]);

            Assert.Empty(HidHideController.ComputeMissing(desired, desired));
            Assert.Empty(HidHideController.ComputeMissing(Array.Empty<string>(), null));
            Assert.Equal(2, HidHideController.ComputeMissing(desired, null).Count);
        }

        /// <summary>THE F20 BUG. The driver copies with strsafe, whose
        /// validator rejects a destination over 32767 characters, and the
        /// old read retried into 65536 bytes (32768 characters) whenever
        /// 4096 bytes was too small. Every list past 2048 characters read
        /// as "driver unreadable" and nothing was hidden. The read now
        /// probes for the exact byte count and allocates that.</summary>
        [Fact]
        public void GetBlacklist_ReadsAListLongerThanTheOldRetryBuffer()
        {
            var fake = new FakeDriver();
            // 250 ids of ~50 characters: 25251 characters in the driver's
            // count, 50502 bytes. Past 4096 bytes, under the strsafe cap.
            // (Anything the driver counts past 32767 characters is a list
            // no client can read, the HidHide CLI included, and is not a
            // PadForge defect.)
            for (int i = 0; i < 250; i++)
                fake.List.Add($@"HID\VID_054C&PID_0CE6&MI_03\8&{i:X8}&0&{i:D4}");
            Assert.True(fake.NeededBytes() > 4096);
            Assert.True(fake.NeededBytes() / 2 <= 32767);

            using (Install(fake))
            {
                var list = HidHideController.GetBlacklist();
                Assert.NotNull(list);
                Assert.Equal(250, list.Count);
                Assert.Equal(fake.List, list);
            }
        }

        /// <summary>An empty list is a 2-byte reply and a successful read
        /// of nothing, never null.</summary>
        [Fact]
        public void GetBlacklist_EmptyListIsEmptyNotNull()
        {
            var fake = new FakeDriver();
            using (Install(fake))
            {
                var list = HidHideController.GetBlacklist();
                Assert.NotNull(list);
                Assert.Empty(list);
            }
        }

        /// <summary>THE F11 BUG. The sync diffed against its own in-process
        /// managed set, so an entry another tool removed (the HidHide
        /// client saves its whole list, a driver-wide replace) was never
        /// re-added: the managed set still listed it, the diff was empty,
        /// and the read-back printed MISSING on every apply. The diff is
        /// now against the driver's list.</summary>
        [Fact]
        public void Sync_ReAddsAnEntryAnotherToolRemoved()
        {
            const string a = @"HID\VID_054C&PID_0CE6&MI_03\8&1";
            var fake = new FakeDriver();
            using (Install(fake))
            {
                var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a };
                Assert.True(HidHideController.SyncManagedDevices(desired, out var added, out var removed));
                Assert.Single(fake.Writes);
                Assert.Contains(a, fake.Writes[0]);
                Assert.Equal(new[] { a }, added);
                Assert.Empty(removed);

                // Another tool saved its whole list without our entry.
                fake.List.Clear();

                Assert.True(HidHideController.SyncManagedDevices(desired, out added, out removed));
                Assert.Equal(2, fake.Writes.Count);
                Assert.Contains(a, fake.Writes[1]);
                Assert.Equal(new[] { a }, added);
                Assert.Contains(a, fake.List);

                // Nothing moved: no write.
                Assert.True(HidHideController.SyncManagedDevices(desired, out added, out removed));
                Assert.Equal(2, fake.Writes.Count);
                Assert.Empty(added);
            }
        }

        /// <summary>Removal is ours only when the driver still carries the
        /// entry, and an entry another tool added is never touched.</summary>
        [Fact]
        public void Sync_RemovesOnlyManagedEntriesTheDriverStillCarries()
        {
            const string a = @"HID\VID_054C&PID_0CE6&MI_03\8&1";
            const string b = @"HID\VID_045E&PID_0B13\7&1";
            const string theirs = @"HID\VID_1234&PID_5678\6&1";
            var fake = new FakeDriver();
            fake.List.Add(theirs);
            using (Install(fake))
            {
                Assert.True(HidHideController.SyncManagedDevices(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a, b }, out _, out _));
                Assert.Equal(new[] { theirs, a, b }, fake.List);

                // B leaves the desired set while the driver carries it.
                Assert.True(HidHideController.SyncManagedDevices(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a }, out var added, out var removed));
                Assert.Empty(added);
                Assert.Equal(new[] { b }, removed);
                Assert.Equal(new[] { theirs, a }, fake.List);

                // A leaves the desired set after another tool already
                // dropped it: nothing to remove, no write, managed set moves on.
                fake.List.Remove(a);
                int writes = fake.Writes.Count;
                Assert.True(HidHideController.SyncManagedDevices(new HashSet<string>(StringComparer.OrdinalIgnoreCase), out added, out removed));
                Assert.Empty(added);
                Assert.Empty(removed);
                Assert.Equal(writes, fake.Writes.Count);
                Assert.Equal(new[] { theirs }, fake.List);
            }
        }

        /// <summary>A refused SET returns false and leaves the managed set
        /// as it was, so the next apply retries the same diff.</summary>
        [Fact]
        public void Sync_RefusedWriteReportsFalseAndRetriesNextTime()
        {
            const string a = @"HID\VID_054C&PID_0CE6&MI_03\8&1";
            var fake = new FakeDriver { RefuseSet = true };
            using (Install(fake))
            {
                var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a };
                Assert.False(HidHideController.SyncManagedDevices(desired, out _, out _));
                Assert.Single(fake.Writes);
                Assert.Empty(fake.List);

                fake.RefuseSet = false;
                Assert.True(HidHideController.SyncManagedDevices(desired, out var added, out _));
                Assert.Equal(2, fake.Writes.Count);
                Assert.Equal(new[] { a }, added);
                Assert.Contains(a, fake.List);
            }
        }

        /// <summary>The controller exposes the probe with its Win32 error,
        /// the read-back, the sync overload that reports its diff and its
        /// result, and the presence probe the sweep gate uses.</summary>
        [Fact]
        public void Controller_ExposesTheDiagnosticSurface()
        {
            string ctl = RepoText("PadForge.App", "Common", "HidHideController.cs");
            Assert.Contains("public static bool TryProbe(out int win32Error)", ctl);
            Assert.Contains("win32Error = Marshal.GetLastWin32Error();", ctl);
            Assert.Contains("public static List<string> MissingFromBlacklist(", ctl);
            Assert.Contains("public static bool SyncManagedDevices(HashSet<string> desiredIds, out List<string> added, out List<string> removed)", ctl);
            Assert.Contains("internal static bool IsInstancePresent(string instanceId)", ctl);
            // Present means present: no phantom flag on the probe.
            Assert.Contains("CM_Locate_DevNodeW(out _, instanceId, 0) == CR_SUCCESS", ctl);
            // The two-call read: a null probe, then the exact size.
            Assert.Contains("if (!TryIo(ioctl, null, null, out int needed) || needed <= 0)", ctl);
            Assert.Contains("byte[] outBuffer = new byte[needed];", ctl);
            Assert.DoesNotContain("new byte[65536]", ctl);
            // The write's result is returned, never discarded.
            Assert.Contains("private static bool SetMultiSzList(uint ioctl, List<string> entries)", ctl);
            Assert.Contains("if (!SetBlacklist(list)) return false;", ctl);
        }

        /// <summary>The hiding path logs: the unavailable case with its
        /// error, the per-device resolution and expansion, the sync diff
        /// with activation, and the read-back with any missing ids. Both
        /// branches sit behind the sole-record gate and say which way it
        /// went, and the block prints only when the desired set moved or
        /// the sync misbehaved, with a once-a-minute heartbeat otherwise.</summary>
        [Fact]
        public void ApplyDeviceHiding_IsObservable()
        {
            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = svc.IndexOf("public void ApplyDeviceHiding()", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = svc.Substring(at, 32000);
            Assert.Contains("HidHideController.TryProbe(out int hidHideErr)", body);
            Assert.Contains("HIDHIDE UNAVAILABLE: hiding requested for", body);
            Assert.Contains("HIDHIDE apply devices=", body);
            Assert.Contains("HIDHIDE dev {ud.VendorId:X4}:{ud.ProdId:X4} id={instanceId} expanded=", body);
            // The first-pass sweep of present nodes sits in the real-path
            // branch, behind the sole-record gate, and reports both outcomes.
            Assert.Contains("bool sweepOn = HidHideSiblingSweepAllowed(snapshot, ud, out int same, null);", body);
            Assert.Contains("foreach (var realId in FindInstanceIdsForDevice(ud))", body);
            Assert.Contains("sweep={sweep.Count}", body);
            Assert.Contains("sweep=off(same={same})", body);
            // The synthetic branch is gated the same way and hides nothing
            // for a record with a present twin, saying so.
            int synthetic = body.IndexOf("// Fallback for synthetic paths", StringComparison.Ordinal);
            Assert.True(synthetic > 0);
            int gate = body.IndexOf("if (!HidHideSiblingSweepAllowed(snapshot, ud, out int syntheticSame, null))", synthetic, StringComparison.Ordinal);
            int resolve = body.IndexOf("var realIds = FindInstanceIdsForDevice(ud);", synthetic, StringComparison.Ordinal);
            Assert.True(gate > synthetic && gate < resolve);
            Assert.Contains("synthetic twin present: not hidden (same={syntheticSame})", body);
            Assert.Contains("HIDHIDE dev {ud.VendorId:X4}:{ud.ProdId:X4} synthetic path=", body);
            Assert.Contains("bool synced = HidHideController.SyncManagedDevices(desiredIds, out var added, out var removed);", body);
            Assert.Contains("MissingFromBlacklist(desiredIds)", body);
            Assert.Contains("readback=MISSING", body);
            Assert.Contains("write=REFUSED", body);
            // Print on movement or trouble, else the heartbeat.
            Assert.Contains("bool desiredMoved = _lastHidHideDesired == null || !_lastHidHideDesired.SetEquals(desiredIds);", body);
            Assert.Contains("bool trouble = !synced || missing == null || missing.Count > 0;", body);
            Assert.Contains("HIDHIDE apply unchanged (n={desiredIds.Count})", body);
            Assert.Contains(">= 60_000", body);
            // The unavailable branch fires only when hiding was wanted.
            Assert.Contains("if (!hidHideUp && wantHiding > 0)", body);
            // The per-row gate still keys on the install scan, unchanged.
            Assert.Contains("row.IsHidHideAvailable = _mainVm.Settings.IsHidHideInstalled;", svc);
        }

        /// <summary>F19: every flip of Step 1's changed flag goes through
        /// MarkChanged, and the naming overload writes one DEVCHG line, so
        /// an idle bench that raises DevicesUpdated every enumeration
        /// interval names the lane and device that flapped. The only bare
        /// assignment left is the helper's own body. The prune that
        /// precedes a re-open logs too, with its lane.</summary>
        [Fact]
        public void Step1_EveryChangeFlipIsNamed()
        {
            string src = RepoText("PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs");
            int bare = src.Split("changed = true;").Length - 1;
            Assert.Equal(1, bare);
            Assert.Contains("private static void MarkChanged(ref bool changed) => changed = true;", src);
            Assert.Contains("private static void MarkChanged(ref bool changed, string lane, string what)", src);
            Assert.Contains("Engine.SdlDiagLog.WriteLine($\"DEVCHG {lane} {what}\");", src);
            // The suspect lane names its device on the re-open.
            Assert.Contains("MarkChanged(ref changed, \"consumer\", $\"+ {cc.Name} handle=0x{cc.Handle.ToInt64():X}\");", src);
            Assert.Contains("PruneOrphanedHandles(_openedConsumerHandles, \"consumer\");", src);
            Assert.Contains("DEVCHG {lane} prune handle=0x", src);
            // Named flips outnumber the silent ones by design: the silent
            // overload is for the four sites a DEV line already covers,
            // plus the naming overload's own call into it.
            int named = src.Split("MarkChanged(ref changed, \"").Length - 1;
            int silent = src.Split("MarkChanged(ref changed);").Length - 1;
            Assert.True(named >= 20, $"named={named}");
            Assert.Equal(5, silent);
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
