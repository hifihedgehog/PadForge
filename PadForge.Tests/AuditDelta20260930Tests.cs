using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.AnalogKeyboard;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Delta audit 2026-09-30 (694f5cbc..6323b062). Behavioral pins for the
    /// fixes that have a seam, and source pins for the ones that do not.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20260930Tests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        /// <summary>A source file with its line endings as LF, whatever the
        /// checkout wrote.</summary>
        private static string Src(string rel)
            => File.ReadAllText(Path.Combine(RepoRoot(), rel)).Replace("\r\n", "\n");

        private static byte[] Png(int width, int height)
        {
            var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static string Package(IEnumerable<(string Name, byte[] Bytes)> entries)
        {
            string path = Path.Combine(Path.GetTempPath(), $"pf-audit-{Guid.NewGuid():N}{IconPackageManager.FileExtension}");
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var (name, bytes) in entries)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                    using var s = entry.Open();
                    s.Write(bytes, 0, bytes.Length);
                }
            }
            return path;
        }

        // ── A picture decodes into its box, holding only its pixels ──────

        /// <summary>DecodePixelWidth alone lets the height follow the aspect
        /// ratio: a 1x20000 PNG came out 80x1600000 (512 MB) at a width of 80,
        /// and a 1x60000 PNG threw OverflowException at a width of 256.</summary>
        [Fact]
        public void ATallPicture_DecodesIntoItsBox()
        {
            var tall = BoundedBitmap.FromBytes(Png(1, 20000), 80, 80);
            Assert.NotNull(tall);
            Assert.Equal(80, tall.PixelHeight);
            Assert.Equal(1, tall.PixelWidth);

            var taller = BoundedBitmap.FromBytes(Png(1, 60000), 256, 256);
            Assert.NotNull(taller);
            Assert.Equal(256, taller.PixelHeight);

            Assert.Null(BoundedBitmap.FromBytes(new byte[] { 1, 2, 3, 4 }, 80, 80));
            Assert.Null(BoundedBitmap.FromBytes(null, 80, 80));
        }

        [Theory]
        // Square and wide pictures decode as a width bound always did.
        [InlineData(100, 100, 80, 80)]
        [InlineData(200, 100, 80, 40)]
        [InlineData(3000, 2, 80, 1)]
        // A taller one fits the square's height now: 40x80 where it was 80x160.
        [InlineData(100, 200, 40, 80)]
        // A small picture still scales up to the box, as it did.
        [InlineData(10, 20, 40, 80)]
        [InlineData(8, 8, 80, 80)]
        public void EveryShape_FitsTheBox(int width, int height, int outWidth, int outHeight)
        {
            var img = BoundedBitmap.FromBytes(Png(width, height), 80, 80);
            Assert.NotNull(img);
            Assert.Equal(outWidth, img.PixelWidth);
            Assert.Equal(outHeight, img.PixelHeight);
            Assert.True(img.IsFrozen);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (BitmapSource Image, WeakReference Bytes) DecodeAndForget()
        {
            var bytes = Png(64, 64);
            return (BoundedBitmap.FromBytes(bytes, 80, 80), new WeakReference(bytes));
        }

        [Fact]
        public void ADecodedPicture_LetsItsEncodedBytesGo()
        {
            // A BitmapImage built on a stream keeps the stream, and the stream
            // keeps the bytes, for as long as the image lives: a cached icon
            // held its whole file.
            var (image, bytes) = DecodeAndForget();
            Assert.NotNull(image);
            for (int i = 0; i < 3 && bytes.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.False(bytes.IsAlive);
            GC.KeepAlive(image);
        }

        [Fact]
        public void ATallPackageIcon_ResolvesWithoutThrowing()
        {
            // MenuIconResolver runs on the 30 Hz UI tick and promises never to
            // throw: OverflowException was in neither loader's filter.
            string file = Package(new[] { ("tall.png", Png(1, 60000)) });
            string name = null;
            try
            {
                name = IconPackageManager.Register(file);
                Assert.NotNull(name);
                var img = MenuIconResolver.Resolve(IconPackageManager.MakeRef(name, "tall.png"));
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(img);
                Assert.True(bitmap.PixelHeight <= 256);
            }
            finally
            {
                if (name != null) IconPackageManager.Unregister(name);
                File.Delete(file);
            }
        }

        // ── The picker reads a package one entry at a time, bounded ──────

        [Fact]
        public void ThePickersRead_StopsAtTheEntryBound_AndHandsEachEntryOnce()
        {
            var entries = Enumerable.Range(0, IconPackageManager.MaxPickerIcons + 5)
                .Select(i => ($"icon{i:D5}.png", new byte[] { (byte)i }));
            string file = Package(entries);
            string name = null;
            try
            {
                name = IconPackageManager.Register(file);
                var seen = new List<string>();
                IconPackageManager.ReadIcons(name, (entry, bytes) => { seen.Add(entry); return true; });
                Assert.Equal(IconPackageManager.MaxPickerIcons, seen.Count);
                Assert.Equal(seen.Count, seen.Distinct().Count());
                Assert.Equal("icon00000.png", seen[0]);
                // The picker's list stops at the same bound, in the same order.
                Assert.Equal(IconPackageManager.ListIcons(name, IconPackageManager.MaxPickerIcons), seen);
                Assert.Equal(IconPackageManager.MaxPickerIcons + 5, IconPackageManager.ListIcons(name).Count);
            }
            finally
            {
                if (name != null) IconPackageManager.Unregister(name);
                File.Delete(file);
            }
        }

        [Fact]
        public void ThePickersRead_StopsAtTheByteBound_CountingEntriesLeftOutForSize()
        {
            // Nine 15 MB entries and a small one: eight fit the 128 MB bound,
            // the ninth is cut short at what is left, and its bytes count, so
            // the read ends there and the small entry after it is not read.
            const int size = 15 * 1024 * 1024;
            var big = new byte[size];
            string file = Package(Enumerable.Range(0, 9).Select(i => ($"big{i}.png", big))
                .Append(("small.png", new byte[] { 1 })));
            string name = null;
            try
            {
                name = IconPackageManager.Register(file);
                var handed = new List<string>();
                long bytes = 0;
                IconPackageManager.ReadIcons(name, (entry, data) => { handed.Add(entry); bytes += data.Length; return true; });
                Assert.Equal(8, handed.Count);
                Assert.DoesNotContain("small.png", handed);
                Assert.True(bytes <= IconPackageManager.MaxPickerBytes);

                // A visitor that says stop ends the read at once.
                int visits = 0;
                IconPackageManager.ReadIcons(name, (entry, data) => ++visits < 3);
                Assert.Equal(3, visits);
            }
            finally
            {
                if (name != null) IconPackageManager.Unregister(name);
                File.Delete(file);
            }
        }

        [Fact]
        public void ThePicker_HoldsAtMostItsThumbnailBound_AcrossPackages()
        {
            // A profile import registers every package it carries, so a bound
            // per package does not bound one opening of the picker.
            var pixel = Png(1, 1);
            // Under each package's own bound, and together past the picker's,
            // so the picker's bound is what stops the third package's read.
            int perPackage = 1500;
            var files = new List<string>();
            var names = new List<string>();
            try
            {
                Assert.True(perPackage < IconPackageManager.MaxPickerIcons);
                Assert.True(2 * perPackage < IconPicker.MaxThumbnails && 3 * perPackage > IconPicker.MaxThumbnails);
                for (int p = 0; p < 3; p++)
                {
                    files.Add(Package(Enumerable.Range(0, perPackage).Select(i => ($"p{p}i{i:D5}.png", pixel))));
                    names.Add(IconPackageManager.Register(files[p]));
                }
                var groups = IconPicker.BuildGroups("");
                int shown = groups.Sum(g => g.Icons.Count);
                Assert.Equal(IconPicker.MaxThumbnails, shown);
                // A second opening reads nothing new and shows the same.
                Assert.Equal(shown, IconPicker.BuildGroups("").Sum(g => g.Icons.Count));
                // The picker keeps its pictures, not a miss per entry it left out.
                Assert.Equal(IconPicker.MaxThumbnails, IconPicker.CachedThumbnailsForTest);
            }
            finally
            {
                foreach (var n in names) if (n != null) IconPackageManager.Unregister(n);
                foreach (var f in files) File.Delete(f);
            }
        }

        [Fact]
        public void APackageLockedAtTheFirstOpening_IsReadAtTheNext()
        {
            string file = Package(new[] { ("one.png", Png(4, 4)) });
            string name = null;
            try
            {
                name = IconPackageManager.Register(file);
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Assert.DoesNotContain(IconPicker.BuildGroups(""), g => g.Name == name);
                }
                var group = Assert.Single(IconPicker.BuildGroups(""), g => g.Name == name);
                Assert.Single(group.Icons);
            }
            finally
            {
                if (name != null) IconPackageManager.Unregister(name);
                File.Delete(file);
            }
        }

        /// <summary>Marks an entry's central directory record as packed by
        /// LZMA (14), a method ZipArchive cannot open.</summary>
        private static void DamageEntry(string file, string entry)
        {
            byte[] b = File.ReadAllBytes(file);
            byte[] name = System.Text.Encoding.UTF8.GetBytes(entry);
            for (int i = 0; i + 46 <= b.Length; i++)
            {
                if (b[i] != 0x50 || b[i + 1] != 0x4B || b[i + 2] != 0x01 || b[i + 3] != 0x02) continue;
                int length = b[i + 28] | (b[i + 29] << 8);
                if (length != name.Length || i + 46 + length > b.Length
                    || !b.AsSpan(i + 46, length).SequenceEqual(name)) continue;
                b[i + 10] = 14;
                b[i + 11] = 0;
                File.WriteAllBytes(file, b);
                return;
            }
            Assert.Fail("no central directory record for " + entry);
        }

        [Fact]
        public void ADamagedEntry_LeavesTheRestOfItsPackageReadable()
        {
            // The whole read used to end at the damaged entry, and the picker
            // showed none of the entries after it.
            string file = Package(new[] { ("a.png", Png(4, 4)), ("b.png", Png(4, 4)), ("c.png", Png(4, 4)) });
            DamageEntry(file, "b.png");
            string name = null;
            try
            {
                name = IconPackageManager.Register(file);
                var seen = new List<string>();
                Assert.True(IconPackageManager.ReadIcons(name, (entry, bytes) => { seen.Add(entry); return true; }));
                Assert.Equal(new[] { "a.png", "c.png" }, seen);
                Assert.Null(IconPackageManager.TryReadIcon(IconPackageManager.MakeRef(name, "b.png")));
                var group = Assert.Single(IconPicker.BuildGroups(""), g => g.Name == name);
                Assert.Equal(new[] { "a.png", "c.png" }, group.Icons.Select(i => i.Name));
            }
            finally
            {
                if (name != null) IconPackageManager.Unregister(name);
                File.Delete(file);
            }
        }

        [Fact]
        public void APackageMissingAtTheFirstOpening_IsReadWhenItReturns()
        {
            // A package on a drive that was not attached yet.
            string file = Package(new[] { ("one.png", Png(4, 4)) });
            string away = file + ".away";
            string name = null;
            try
            {
                name = IconPackageManager.Register(file);
                File.Move(file, away);
                Assert.DoesNotContain(IconPicker.BuildGroups(""), g => g.Name == name);
                File.Move(away, file);
                var group = Assert.Single(IconPicker.BuildGroups(""), g => g.Name == name);
                Assert.Single(group.Icons);
            }
            finally
            {
                if (name != null) IconPackageManager.Unregister(name);
                if (File.Exists(away)) File.Move(away, file, overwrite: true);
                File.Delete(file);
            }
        }

        // ── A failed probe is tried again ─────────────────────────────────

        [Fact]
        public void AFailedAnalogKeyboardProbe_IsTriedAgain_AndAGoodOneIsKept()
        {
            string path = $@"\\?\hid#audit-{Guid.NewGuid():N}";
            int probes = 0;
            var info = new AnalogKeyboardDeviceInfo { Path = path };
            AnalogKeyboardDeviceInfo Probe() => ++probes == 1 ? null : info;
            try
            {
                Assert.Null(AnalogKeyboardHidRuntime.Lookup(path, Probe));
                Assert.Same(info, AnalogKeyboardHidRuntime.Lookup(path, Probe));
                Assert.Same(info, AnalogKeyboardHidRuntime.Lookup(path, Probe));
                Assert.Equal(2, probes);
            }
            finally
            {
                AnalogKeyboardHidRuntime.InvalidateCache();
            }

            // Value caps it declares but could not read fail the probe, so it
            // is tried again rather than kept with none.
            string analog = Src(Path.Combine("PadForge.App", "Common", "Input", "AnalogKeyboardHid.cs"));
            Assert.Contains("if (valueCaps == null) return null;", analog);
        }

        [Fact]
        public void AnUnreadableVendorCollection_IsTriedAgain_AndAJudgedOneIsKept()
        {
            string unreadable = $@"\\?\hid#audit-{Guid.NewGuid():N}";
            string notVendor = $@"\\?\hid#audit-{Guid.NewGuid():N}";
            int probes = 0;
            var verdict = new VendorHidCollection { Path = unreadable };
            try
            {
                Assert.Null(VendorHidRuntime.Lookup(unreadable, () => { probes++; return (null, true); }));
                Assert.Same(verdict, VendorHidRuntime.Lookup(unreadable, () => { probes++; return (verdict, false); }));
                Assert.Same(verdict, VendorHidRuntime.Lookup(unreadable, () => { probes++; return (null, true); }));
                Assert.Equal(2, probes);

                // A collection judged not to be a vendor one stays judged.
                Assert.Null(VendorHidRuntime.Lookup(notVendor, () => { probes++; return (null, false); }));
                Assert.Null(VendorHidRuntime.Lookup(notVendor, () => { probes++; return (verdict, false); }));
                Assert.Equal(3, probes);
            }
            finally
            {
                VendorHidRuntime.InvalidateCache();
            }

            // Its identity comes from HidD_GetAttributes, so a failed read is
            // one to try again, never a collection filed under VID and PID 0.
            string vendor = Src(Path.Combine("PadForge.App", "Common", "Input", "VendorHidRuntime.cs"));
            Assert.Contains("if (!SonyHeadsetHid.HidD_GetAttributes(handle, ref attributes))\n                {\n                    unreadable = true;", vendor);
        }

        // ── The USIO reopen runs across an SDL update ─────────────────────

        [Fact]
        public void TheUsioReopen_TurnsTheDriverOffAndOnAcrossAnUpdate()
        {
            long saved = InputManager.UsioReopenStateForTest;
            try
            {
                // Each loop run holds its own token. Each step stands before one
                // of the loop's SDL updates, so an "update" entry follows each
                // step, as in the poll loop.
                long run = InputManager.NewUsioOwner();
                long stale = InputManager.NewUsioOwner();
                Assert.NotEqual(run, stale);
                var log = new List<string>();
                void Poll(long owner, bool accept = true)
                {
                    InputManager.AdvanceUsioReopen(owner, v => { log.Add(v); return accept; });
                    log.Add("update");
                }
                InputManager.UsioReopenStateForTest = 0;
                Poll(run);
                Assert.Equal(new[] { "update" }, log);

                log.Clear();
                InputManager.RequestUsioReopen();
                Poll(run);
                Poll(run);
                Poll(run);
                Assert.Equal(new[] { "0", "update", "1", "update", "update" }, log);

                // A request while the driver is off runs another whole cycle.
                log.Clear();
                InputManager.RequestUsioReopen();
                Poll(run);
                InputManager.RequestUsioReopen();
                Poll(run);
                Poll(run);
                Assert.Equal(new[] { "0", "update", "0", "update", "1", "update" }, log);
                Assert.Equal(0, InputManager.UsioReopenStateForTest);

                // A loop that outlived Stop's join passed its stamp check before
                // the next run began. It finds the current loop's off and takes
                // the cycle over without writing, and the current loop writes
                // the on only after an update of its own has followed its taking
                // it back.
                log.Clear();
                InputManager.RequestUsioReopen();
                InputManager.AdvanceUsioReopen(run, v => { log.Add(v); return true; });
                InputManager.AdvanceUsioReopen(stale, v => { log.Add("stale " + v); return true; });
                log.Add("update");
                Poll(run);
                Poll(run);
                Assert.Equal(new[] { "0", "update", "update", "1", "update" }, log);
                Assert.Equal(0, InputManager.UsioReopenStateForTest);

                // An off SDL refuses ends the cycle with nothing changed.
                log.Clear();
                InputManager.RequestUsioReopen();
                Poll(run, accept: false);
                Assert.Equal(0, InputManager.UsioReopenStateForTest);
                Poll(run);
                Assert.Equal(new[] { "0", "update", "update" }, log);

                // An on SDL refuses is written again: the driver must not stay off.
                log.Clear();
                InputManager.RequestUsioReopen();
                Poll(run);
                Poll(run, accept: false);
                Assert.Equal(InputManager.UsioOffBy(run), InputManager.UsioReopenStateForTest);
                Poll(run);
                Assert.Equal(new[] { "0", "update", "1", "update", "1", "update" }, log);
                Assert.Equal(0, InputManager.UsioReopenStateForTest);

                // In the loop, each step comes right before an SDL update, only
                // for the current run and under that run's own token, and SDL's
                // own initialization clears it.
                string loop = Src(Path.Combine("PadForge.App", "Common", "Input", "InputManager.cs"));
                Assert.Contains("long usioOwner = NewUsioOwner();", loop);
                Assert.Contains("if (System.Threading.Volatile.Read(ref _runGeneration) == generation) AdvanceUsioReopen(usioOwner);\n                            long tsIdleSdl = Stopwatch.GetTimestamp();\n                            SDL_UpdateJoysticks();", loop);
                Assert.Contains("if (System.Threading.Volatile.Read(ref _runGeneration) == generation) AdvanceUsioReopen(usioOwner);\n                        long tsSdl = Stopwatch.GetTimestamp();\n                        SDL_UpdateJoysticks();", loop);
                Assert.Contains("SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_USIO_LAYOUT, _usioLayout);\n                ResetUsioReopenAtInit(value => SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_USIO, value));", loop);
                Assert.DoesNotContain("Thread.Sleep(200);\n                WriteHintUnderJoystickLock(SDL_HINT_JOYSTICK_HIDAPI_USIO", loop);
            }
            finally
            {
                InputManager.UsioReopenStateForTest = saved;
            }
        }

        /// <summary>Runs <paramref name="stalled"/> on a thread that stops inside
        /// its hint write, the lock held, then <paramref name="contender"/> on a
        /// second thread, and reports whether the contender finished while the
        /// first was still stopped. The contender signals just before it asks
        /// for the lock, so its silence afterward is the lock's doing. Both
        /// threads are released and joined whatever the test finds.</summary>
        private static bool ContenderFinishedWhileStalled(Action<Func<string, bool>> stalled, Action contender,
            List<string> writes)
        {
            using var inside = new ManualResetEventSlim();
            using var go = new ManualResetEventSlim();
            using var asking = new ManualResetEventSlim();
            using var done = new ManualResetEventSlim();
            var first = new Thread(() => stalled(v =>
            {
                inside.Set();
                go.Wait(5000);
                lock (writes) writes.Add(v);
                return true;
            })) { IsBackground = true };
            var second = new Thread(() => { asking.Set(); contender(); done.Set(); }) { IsBackground = true };
            try
            {
                first.Start();
                Assert.True(inside.Wait(5000));
                second.Start();
                Assert.True(asking.Wait(5000));
                return done.Wait(300);
            }
            finally
            {
                go.Set();
                bool firstJoined = Joined(first);
                bool secondJoined = Joined(second);
                Assert.True(firstJoined);
                Assert.True(secondJoined);
            }
        }

        /// <summary>True once <paramref name="thread"/> has ended, or when it
        /// never started: a failed wait leaves the second thread unstarted, and
        /// joining it would throw over the failure.</summary>
        private static bool Joined(Thread thread)
            => (thread.ThreadState & ThreadState.Unstarted) != 0 || thread.Join(5000);

        [Fact]
        public void AStalledStep_CannotLandItsOff_AfterAnotherLoopFinishedTheCycle()
        {
            // A retired loop read "asked" and stalled inside its hint write,
            // which lands when the call returns. Without the lock the current
            // loop ran the whole cycle meanwhile, and the stale off landed
            // last, leaving the driver off and the state idle.
            long saved = InputManager.UsioReopenStateForTest;
            try
            {
                long run = InputManager.NewUsioOwner();
                long stale = InputManager.NewUsioOwner();
                var writes = new List<string>();
                InputManager.UsioReopenStateForTest = 0;
                InputManager.RequestUsioReopen();
                bool finished = ContenderFinishedWhileStalled(
                    write => InputManager.AdvanceUsioReopen(stale, write),
                    () =>
                    {
                        for (int i = 0; i < 3; i++)
                            InputManager.AdvanceUsioReopen(run, v => { lock (writes) writes.Add(v); return true; });
                    },
                    writes);
                Assert.False(finished);
                // The stale off landed first, and the current loop took the
                // cycle over and wrote the on last.
                lock (writes) Assert.Equal(new[] { "0", "1" }, writes);
                Assert.Equal(0, InputManager.UsioReopenStateForTest);
            }
            finally
            {
                InputManager.UsioReopenStateForTest = saved;
            }
        }

        [Fact]
        public void SdlInitialization_WaitsForAStepInItsWrite_AndTurnsTheDriverBackOn()
        {
            // A loop that outlived its engine read "asked" and stalled inside
            // its write while the next engine initialized SDL. Reset outside
            // the lock, the state went idle first, the off landed after, and
            // no step would ever turn the driver back on.
            long saved = InputManager.UsioReopenStateForTest;
            try
            {
                long stale = InputManager.NewUsioOwner();
                var writes = new List<string>();
                InputManager.UsioReopenStateForTest = 0;
                InputManager.RequestUsioReopen();
                bool finished = ContenderFinishedWhileStalled(
                    write => InputManager.AdvanceUsioReopen(stale, write),
                    () => InputManager.ResetUsioReopenAtInit(v => { lock (writes) writes.Add(v); return true; }),
                    writes);
                Assert.False(finished);
                lock (writes) Assert.Equal(new[] { "0", "1" }, writes);
                Assert.Equal(0, InputManager.UsioReopenStateForTest);

                // Nothing pending: initialization writes nothing.
                writes.Clear();
                InputManager.ResetUsioReopenAtInit(v => { writes.Add(v); return true; });
                Assert.Empty(writes);
            }
            finally
            {
                InputManager.UsioReopenStateForTest = saved;
            }
        }

        // ── A retired poll loop leaves a newer run's latches alone ────────

        [Fact]
        public void ARetiredLoopsExit_LeavesTheLatchesToANewerRun()
        {
            // VK_NONAME (0xFC): routed through SendInput, types nothing.
            var im = new InputManager();
            im._latchedKeysDown.Add(0xFC);

            // Run 1 stopped (stamp 2) and run 3 started before its finally.
            im.RunGenerationForTest = 3;
            im.ReleaseLatchesOnExit(1);
            Assert.Contains((ushort)0xFC, im._latchedKeysDown);

            // Stopped with no newer run: the exit releases.
            im.RunGenerationForTest = 2;
            im.ReleaseLatchesOnExit(1);
            Assert.Empty(im._latchedKeysDown);

            // An exit with no stop at all (the loop threw) releases too.
            im._latchedKeysDown.Add(0xFC);
            im.RunGenerationForTest = 1;
            im.ReleaseLatchesOnExit(1);
            Assert.Empty(im._latchedKeysDown);
        }

        [Fact]
        public void ANewLoop_ReleasesWhatARetiredRunLeft_OnlyWhileItsRunIsCurrent()
        {
            var im = new InputManager();
            im._latchedKeysDown.Add(0xFC);
            im.RunGenerationForTest = 3;
            im.ReleaseInheritedLatches(3);
            Assert.Empty(im._latchedKeysDown);

            // A thread of run 3 slow to start after run 3 stopped and run 5
            // began and latched a key: run 5's latch stays.
            im._latchedKeysDown.Add(0xFC);
            im.RunGenerationForTest = 5;
            im.ReleaseInheritedLatches(3);
            Assert.Contains((ushort)0xFC, im._latchedKeysDown);

            string loop = Src(Path.Combine("PadForge.App", "Common", "Input", "InputManager.cs"));
            Assert.Contains("ReleaseInheritedLatches(generation);", loop);
            Assert.Contains("ReleaseLatchesOnExit(generation);", loop);
            Assert.Contains("int generation = StampRun();", loop);
        }

        [Fact]
        public void AnExitRelease_HoldsOffTheNextRunsStamp_UntilItIsDone()
        {
            // Without the lock, an exit that passed its check could pause while
            // Start stamped the next run, then release that run's latches.
            var im = new InputManager();
            im._latchedKeysDown.Add(0xFC);
            im.RunGenerationForTest = 2; // run 1 stopped
            using var inside = new ManualResetEventSlim();
            using var go = new ManualResetEventSlim();
            using var asking = new ManualResetEventSlim();
            using var startDone = new ManualResetEventSlim();
            im.ExitReleaseCheckedForTest = () => { inside.Set(); go.Wait(5000); };
            int stamped = 0;
            var exit = new Thread(() => im.ReleaseLatchesOnExit(1)) { IsBackground = true };
            var start = new Thread(() => { asking.Set(); stamped = im.StampRun(); startDone.Set(); }) { IsBackground = true };
            try
            {
                exit.Start();
                Assert.True(inside.Wait(5000));
                start.Start();
                Assert.True(asking.Wait(5000));
                // The stamp waits for the release in progress.
                Assert.False(startDone.Wait(300));
                Assert.Equal(2, im.RunGenerationForTest);
            }
            finally
            {
                go.Set();
                bool exitJoined = Joined(exit);
                bool startJoined = Joined(start);
                Assert.True(exitJoined);
                Assert.True(startJoined);
            }
            Assert.Equal(3, stamped);
            Assert.Empty(im._latchedKeysDown);
        }

        // ── Remote Link carries what the peer cannot work out ─────────────

        private static RemotePeerDeviceInfo Pad(string id, string name = "Xbox Wireless Controller") => new()
        {
            Slot = 0,
            PeerLocalDeviceId = id,
            Name = name,
            VendorId = 0x045E,
            ProductId = 0x0B13,
            NumAxes = 6,
            NumButtons = 22,
            RawButtonCount = 22,
            SupportedButtonIndices = Enumerable.Range(0, 15).ToArray(),
            SupportedAxisIndices = Enumerable.Range(0, 6).ToArray(),
            SdlGuid = "030000005e040000130b000001050000",
            InputDeviceType = InputDeviceType.Gamepad,
        };

        /// <summary>Where the tails after v8 begin: the same list with neither
        /// new field set encodes up to there and stops.</summary>
        private static int NewTailsStart(IEnumerable<RemotePeerDeviceInfo> devices)
            => LinkConnection.EncodeDeviceList(devices.Select(d =>
            {
                var bare = d.CloneForSlot(d.Slot);
                bare.AnalogKeyOrder = null;
                bare.BlissBoxRestMask = null;
                return bare;
            }).ToList(), "").Length;

        [Fact]
        public void TheOwnersKeyOrderAndRestMask_CrossTheWire()
        {
            var keyboard = Pad("kb");
            keyboard.InputDeviceType = InputDeviceType.AnalogKeyboard;
            // Descending, with a repeat and two codes out of range.
            var order = new[] { AnalogKeyCodes.PositionBase + 37, AnalogKeyCodes.W, AnalogKeyCodes.W, 0, AnalogKeyInputState.CodeCount };
            var before = (int[])order.Clone();
            keyboard.AnalogKeyOrder = order;
            var port = Pad("port");
            port.InputDeviceType = InputDeviceType.Joystick;
            port.BlissBoxRestMask = (1 << 2) | (1 << 5);
            var plain = Pad("pad");
            var devices = new[] { plain, keyboard, port };

            var payload = LinkConnection.EncodeDeviceList(devices, "");
            // The owner's array is left as it was.
            Assert.Equal(before, order);
            var list = LinkConnection.DecodeDeviceList(payload);
            Assert.Null(list[0].AnalogKeyOrder);
            Assert.Null(list[0].BlissBoxRestMask);
            // In range and once each, in the owner's order.
            Assert.Equal(new[] { AnalogKeyCodes.PositionBase + 37, AnalogKeyCodes.W }, list[1].AnalogKeyOrder);
            Assert.Null(list[1].BlissBoxRestMask);
            Assert.Null(list[2].AnalogKeyOrder);
            Assert.Equal((byte)((1 << 2) | (1 << 5)), list[2].BlissBoxRestMask);

            // An older sender stops after v8: both stay null.
            int start = NewTailsStart(devices);
            Assert.Equal(0xEA, payload[start]);
            var oldList = LinkConnection.DecodeDeviceList(payload.Take(start).ToArray());
            Assert.All(oldList, d => Assert.Null(d.AnalogKeyOrder));
            Assert.All(oldList, d => Assert.Null(d.BlissBoxRestMask));
            Assert.Equal("Xbox Wireless Controller", oldList[0].Name);

        }

        /// <summary>The bytes the encoder at 6323b062, before the new tails
        /// and the whole-payload budget, gave for <see cref="GoldenList"/>.</summary>
        private const string GoldenDeviceListHex =
            "020320003031323334353637383961626364656630313233343536373839616263646566180058626F7820576972656C65737320436F6E74726F6C6C65725E04130B06160041150007200066656463626139383736353433323130666564636261393837363534333231301000436F6E73756D657220436F6E74726F6C6D042BC5000300501D00E200000000000300534E31010202000A00506C61792F50617573650000000004000000000000000000000000000000000000000900566F6C756D65205570010000000400000000000000000000000000000000000000E31603E40000E50000E6060044656E205043E702FF7F00E80020003033303030303030356530343030303031333062303030303031303530303030000000E90000";

        private static RemotePeerDeviceInfo[] GoldenList() => new[]
        {
            new RemotePeerDeviceInfo { Slot = 3, PeerLocalDeviceId = "0123456789abcdef0123456789abcdef", Name = "Xbox Wireless Controller", VendorId = 0x045E, ProductId = 0x0B13, NumAxes = 6, NumButtons = 22, RawButtonCount = 22, SupportedButtonIndices = Enumerable.Range(0, 15).ToArray(), SupportedAxisIndices = Enumerable.Range(0, 6).ToArray(), SdlGuid = "030000005e040000130b000001050000", InputDeviceType = InputDeviceType.Gamepad, HasRumble = true },
            new RemotePeerDeviceInfo { Slot = 7, PeerLocalDeviceId = "fedcba9876543210fedcba9876543210", Name = "Consumer Control", VendorId = 0x046D, ProductId = 0xC52B, NumButtons = 3, RawButtonCount = 3, SerialNumber = "SN1", NumTouchpads = 1, TouchpadFingerCounts = new[] { 2 }, HasTouchpad = true, InputDeviceType = InputDeviceType.ConsumerControl, DeviceObjects = new[] { new DeviceObjectItem { Name = "Play/Pause", InputIndex = 0, ObjectType = DeviceObjectTypeFlags.PushButton }, new DeviceObjectItem { Name = "Volume Up", InputIndex = 1, ObjectType = DeviceObjectTypeFlags.PushButton } } },
        };

        [Fact]
        public void AListWithoutTheNewFields_EncodesByteForByteAsBefore()
        {
            // Captured from the encoder before this change: every section, the
            // names and each tail up to v8.
            var bytes = LinkConnection.EncodeDeviceList(GoldenList(), "Den PC");
            Assert.Equal(GoldenDeviceListHex, Convert.ToHexString(bytes));
        }

        [Fact]
        public void EachNewTail_StandsAlone_AndCarriesEmptyAndZero()
        {
            // v10 without v9, and a present empty key list and zero mask,
            // which say something different from no field at all.
            var port = Pad("port");
            port.BlissBoxRestMask = 0;
            var list = LinkConnection.DecodeDeviceList(LinkConnection.EncodeDeviceList(new[] { port }, ""));
            Assert.Equal((byte)0, list[0].BlissBoxRestMask);
            Assert.Null(list[0].AnalogKeyOrder);

            var keyboard = Pad("kb");
            keyboard.AnalogKeyOrder = Array.Empty<int>();
            var list2 = LinkConnection.DecodeDeviceList(LinkConnection.EncodeDeviceList(new[] { keyboard }, ""));
            Assert.NotNull(list2[0].AnalogKeyOrder);
            Assert.Empty(list2[0].AnalogKeyOrder);
            Assert.Null(list2[0].BlissBoxRestMask);
        }

        [Fact]
        public void AMalformedKeyOrder_CostsTheOrdersAndTheTailAfterIt()
        {
            var keyboard = Pad("kb");
            keyboard.AnalogKeyOrder = new[] { AnalogKeyCodes.W, AnalogKeyCodes.S };
            keyboard.BlissBoxRestMask = 4;
            var devices = new[] { keyboard };
            int v9 = NewTailsStart(devices);

            // [magic][records][index][u16 count][u16 W][u16 S]: zero the first code.
            var payload = LinkConnection.EncodeDeviceList(devices, "");
            payload[v9 + 5] = 0;
            payload[v9 + 6] = 0;
            var list = LinkConnection.DecodeDeviceList(payload);
            Assert.Null(list[0].AnalogKeyOrder);
            Assert.Null(list[0].BlissBoxRestMask);
            Assert.Equal("Xbox Wireless Controller", list[0].Name);

            // A repeated record index costs the orders too.
            var twice = Pad("kb2");
            twice.AnalogKeyOrder = new[] { AnalogKeyCodes.W };
            var two = new[] { keyboard, twice };
            int v9b = NewTailsStart(two);
            var repeated = LinkConnection.EncodeDeviceList(two, "");
            // The second record starts after the first's 3 + 2 x 2 bytes.
            repeated[v9b + 2 + 3 + 4] = 0;
            Assert.All(LinkConnection.DecodeDeviceList(repeated), d => Assert.Null(d.AnalogKeyOrder));

            // A mask record past the device count costs the masks alone.
            var good = LinkConnection.EncodeDeviceList(devices, "");
            int v10 = v9 + 2 + 3 + 4;
            Assert.Equal(0xEB, good[v10]);
            good[v10 + 2] = 7;
            var list2 = LinkConnection.DecodeDeviceList(good);
            Assert.Equal(new[] { AnalogKeyCodes.W, AnalogKeyCodes.S }, list2[0].AnalogKeyOrder);
            Assert.Null(list2[0].BlissBoxRestMask);
        }

        [Fact]
        public void AFailedKeyOrderTail_StopsTheDecoderBeforeTheNext()
        {
            // The cursor stands wherever v9 failed. Read on from there, the
            // bytes of a bad record can pass for the v10 magic: here the index
            // fails, and the count and first code that follow read as a v10
            // record giving the device a mask of 9.
            var keyboard = Pad("kb");
            keyboard.AnalogKeyOrder = new[] { AnalogKeyCodes.W, AnalogKeyCodes.S };
            keyboard.BlissBoxRestMask = 4;
            var devices = new[] { keyboard };
            int v9 = NewTailsStart(devices);
            var payload = LinkConnection.EncodeDeviceList(devices, "");
            payload[v9 + 2] = 7;     // the record's index, past the count
            payload[v9 + 3] = 0xEB;  // count low: reads as the v10 magic
            payload[v9 + 4] = 1;     // count high: one record
            payload[v9 + 5] = 0;     // first code low: record index 0
            payload[v9 + 6] = 9;     // first code high: mask 9
            var list = LinkConnection.DecodeDeviceList(payload);
            Assert.Null(list[0].AnalogKeyOrder);
            Assert.Null(list[0].BlissBoxRestMask);
        }

        [Fact]
        public void TheDeviceListBudget_CoversTheWholePayload()
        {
            // A G-keys row publishes 102 named buttons. Its names took the
            // room left at its own position, and every later section and tail
            // came after that unchecked, past the 4 KB an older peer reads.
            var gkeys = Pad("0123456789abcdef0123456789abcdef", "Logitech G915 G-Keys");
            gkeys.InputDeviceType = InputDeviceType.LogitechGKeys;
            gkeys.DeviceObjects = Enumerable.Range(0, 102).Select(i => new DeviceObjectItem
            {
                Name = $"G{i + 1} (M1)",
                InputIndex = i,
                ObjectType = DeviceObjectTypeFlags.PushButton,
            }).ToArray();
            var devices = new List<RemotePeerDeviceInfo> { gkeys };
            for (int i = 0; i < 20; i++)
                devices.Add(Pad(Guid.NewGuid().ToString("N")));

            var payload = LinkConnection.EncodeDeviceList(devices, "Living Room PC");
            Assert.True(payload.Length <= 3800, $"payload {payload.Length} bytes");
            var list = LinkConnection.DecodeDeviceList(payload);
            Assert.Equal(21, list.Count);
            Assert.NotEmpty(list[0].DeviceObjects);
            Assert.Equal("030000005e040000130b000001050000", list[20].SdlGuid);
        }

        [Fact]
        public void TheNewTails_NeverPushAListPastTheBudget()
        {
            // Thirty pads leave room for a rest mask but not for a 600-key
            // list: the list rides without its key order, the mask with it.
            var devices = new List<RemotePeerDeviceInfo>();
            for (int i = 0; i < 30; i++)
                devices.Add(Pad(Guid.NewGuid().ToString("N")));
            var keyboard = devices[0];
            keyboard.AnalogKeyOrder = Enumerable.Range(1, 600).ToArray();
            devices[1].BlissBoxRestMask = 4;

            int bare = NewTailsStart(devices);
            Assert.True(bare + 1200 > 3800 && bare + 4 <= 3800, $"fixture base {bare} bytes");
            var payload = LinkConnection.EncodeDeviceList(devices, "");
            Assert.True(payload.Length <= 3800, $"payload {payload.Length} bytes");
            var list = LinkConnection.DecodeDeviceList(payload);
            Assert.Null(list[0].AnalogKeyOrder);
            Assert.Equal((byte)4, list[1].BlissBoxRestMask);
        }

        [Fact]
        public void APeersAnalogKeyboard_ListsTheOwnersKeys()
        {
            var order = new[] { AnalogKeyCodes.W, AnalogKeyCodes.PositionBase + 12 };
            var info = Pad("kb");
            info.InputDeviceType = InputDeviceType.AnalogKeyboard;
            info.VendorId = 0x31E3;
            info.ProductId = 0x1232;
            info.AnalogKeyOrder = order;
            var ud = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                VendorId = 0x31E3,
                ProdId = 0x1232,
                CapType = InputDeviceType.AnalogKeyboard,
                Device = new RemotePeerDevice(info),
            };
            Assert.Equal(order, AnalogKeyboardRuntime.KeysFor(ud));

            // From an owner that sends no list: the catalog, as before.
            info.AnalogKeyOrder = null;
            Assert.Equal(AnalogKeyboardCatalog.KeysFor(0x31E3, 0x1232), AnalogKeyboardRuntime.KeysFor(ud));

            // The owner fills both fields, and the peer refreshes them in place
            // and says when a key list changed.
            string service = Src(Path.Combine("PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("? PadForge.Common.Input.AnalogKeyboardRuntime.KeysFor(ud)\n                                    : null,", service);
            Assert.Contains("BlissBoxRestMask = PadForge.Common.Input.BlissBoxRuntime.NativeRestMask(ud),", service);
            Assert.Contains("_linkServer.DeviceKeyOrderChanged += _ => OnAnalogKeyOrdersChanged(null, EventArgs.Empty);", service);
            Assert.Contains("PadForge.Common.Input.AnalogKeyboardRuntime.KeyOrdersChanged += OnAnalogKeyOrdersChanged;", service);
            string server = Src(Path.Combine("PadForge.Engine", "RemoteLink", "LinkServer.cs"));
            Assert.Contains("existing.Info.AnalogKeyOrder = info.AnalogKeyOrder;", server);
            Assert.Contains("existing.Info.BlissBoxRestMask = info.BlissBoxRestMask;", server);
            Assert.Contains("if (keysChanged) notifications.Add(() => DeviceKeyOrderChanged?.Invoke(existing));", server);
        }

        // ── A key list that changes rebuilds what is built from it ────────

        [Fact]
        public void AKeyListThatChanges_SaysSo_AndOneThatDoesNotStaysQuiet()
        {
            var row = Guid.NewGuid();
            int raised = 0;
            EventHandler count = (_, __) => raised++;
            AnalogKeyboardRuntime.KeyOrdersChanged += count;
            try
            {
                AnalogKeyboardRuntime.SetKeyOrder(row, new[] { AnalogKeyCodes.W });
                Assert.Equal(1, raised);
                AnalogKeyboardRuntime.SetKeyOrder(row, new[] { AnalogKeyCodes.W });
                Assert.Equal(1, raised);
                AnalogKeyboardRuntime.SetKeyOrder(row, new[] { AnalogKeyCodes.W, AnalogKeyCodes.S });
                Assert.Equal(2, raised);
            }
            finally
            {
                AnalogKeyboardRuntime.KeyOrdersChanged -= count;
            }
        }

        [Fact]
        public void ThePreview_ReordersItsChips_ToANewKeyOrder()
        {
            var vm = new DevicesViewModel();
            vm.SetAnalogKeyOrder(new[] { AnalogKeyCodes.W });
            var keys = new AnalogKeyInputState();
            keys.Set(AnalogKeyCodes.PositionBase + 5, 0.5f);
            keys.Set(AnalogKeyCodes.W, 0.5f);
            vm.UpdateAnalogKeys(keys);
            // The position-coded key is not in the order yet: it goes last.
            Assert.Equal(new[] { AnalogKeyCodes.W, AnalogKeyCodes.PositionBase + 5 }, vm.AnalogKeys.Select(k => k.Code));

            vm.SetAnalogKeyOrder(new[] { AnalogKeyCodes.PositionBase + 5, AnalogKeyCodes.W });
            Assert.Equal(new[] { AnalogKeyCodes.PositionBase + 5, AnalogKeyCodes.W }, vm.AnalogKeys.Select(k => k.Code));
            Assert.Equal(new[] { 0, 1 }, vm.AnalogKeys.Select(k => k.Rank));
        }

        // ── The PS Move's ID read repeats its cancel ─────────────────────

        [Fact]
        public void TheIdReadsCancel_RepeatsPastItsDeadline()
        {
            string move = Src(Path.Combine("PadForge.App", "Common", "Input", "PsMoveDirectService.cs"));
            Assert.Contains("new Timer(_ => CancelIoEx(h, IntPtr.Zero), null, ExtReplyTimeoutMs, ExtCancelRepeatMs);", move);
            Assert.Contains("() => !_writerRun || Environment.TickCount64 >= deadline);", move);
        }
    }
}
