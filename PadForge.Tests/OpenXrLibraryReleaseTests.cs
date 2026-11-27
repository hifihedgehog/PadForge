using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PadForge.Common;
using PadForge.Engine.Common.OpenXr;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Uninstall SteamVR frees the OpenXR runtime library PadForge loaded from
    /// the SteamVR folder, only after the head-tracker sources that took it
    /// stopped, leaves a source on another runtime running, and refuses the
    /// library for the rest of the process. The handles are fake, the free is
    /// handed in, and a source's opener is scripted, so nothing here loads a
    /// library or reaches a runtime.
    /// </summary>
    public class OpenXrLibraryReleaseTests
    {
        private static string NewFolder()
            => Path.Combine(Path.GetTempPath(), "PadForgeTests", "OpenXrRelease-" + Guid.NewGuid().ToString("N"));

        private static string Library(string folder) => Path.Combine(folder, "bin", "win64", "vrclient_x64.dll");

        /// <summary>A source whose opener takes the library on the source's own
        /// thread, sets <paramref name="opened"/>, and returns once the source
        /// is stopped, or once <paramref name="release"/> is set when one is
        /// given.</summary>
        private static OpenXrHeadPoseSource HoldingSource(string library, ManualResetEventSlim opened,
                                                          ManualResetEventSlim release = null)
        {
            OpenXrHeadPoseSource source = null;
            source = new OpenXrHeadPoseSource(() => null, _ => { })
            {
                OpenLink = (string manifest, bool again, Action<string> log,
                            out OpenXrSourceState failure, out bool unavailable, out string read) =>
                {
                    OpenXrSession.LoadLibraryOnce(library, _ => (IntPtr)0x5200, out _);
                    opened.Set();
                    if (release != null) release.Wait(10000);
                    else while (source.Running) Thread.Sleep(5);
                    failure = OpenXrSourceState.NoRuntime;
                    unavailable = false;
                    read = "scripted";
                    return null;
                },
            };
            // A source that ends on its stop gets a join no loaded PC
            // outlasts, so the free never waits on how long it takes.
            if (release == null) source.StopJoinMs = 30000;
            return source;
        }

        [Fact]
        public void TheUninstall_FreesTheLibrary_OnlyAfterTheSourceStopped()
        {
            string folder = NewFolder();
            OpenXrSession.LoadLibraryOnce(Library(folder), _ => (IntPtr)0x5101, out _);
            var order = new List<string>();
            OpenXrSession.ReleaseLibrariesUnder(folder,
                holders =>
                {
                    Assert.Contains(Thread.CurrentThread, holders);
                    order.Add("stop");
                    return true;
                },
                module => order.Add("free " + module.ToInt64()));
            Assert.Equal(new[] { "stop", "free " + 0x5101 }, order);
        }

        [Fact]
        public void AStopThatFails_LeavesTheLibraryLoaded_AndSaysItIsStillHeld()
        {
            string folder = NewFolder();
            OpenXrSession.LoadLibraryOnce(Library(folder), _ => (IntPtr)0x5102, out _);
            bool freed = false;
            OpenXrSession.ReleaseLibrariesUnder(folder, _ => false, _ => freed = true);
            Assert.False(freed);
            Assert.Equal(IntPtr.Zero, OpenXrSession.LoadLibraryOnce(Library(folder), _ => (IntPtr)0x5108,
                out OpenXrSession.LibraryRefusal refusal));
            Assert.Equal(OpenXrSession.LibraryRefusal.StillHeld, refusal);
            Assert.Contains("still held", OpenXrSession.RefusedRead("SteamVR", Library(folder), refusal));
        }

        [Fact]
        public void ASecondLoadAfterTheFree_IsRefused_AndSaysItWasUnloaded()
        {
            string folder = NewFolder();
            int loads = 0;
            OpenXrSession.LoadLibraryOnce(Library(folder), _ => { loads++; return (IntPtr)0x5103; }, out _);
            OpenXrSession.ReleaseLibrariesUnder(folder, _ => true, _ => { });
            IntPtr again = OpenXrSession.LoadLibraryOnce(Library(folder), _ => { loads++; return (IntPtr)0x5104; },
                out OpenXrSession.LibraryRefusal refusal);
            Assert.Equal(OpenXrSession.LibraryRefusal.Unloaded, refusal);
            Assert.Equal(IntPtr.Zero, again);
            Assert.Equal(1, loads);
            Assert.Contains("was unloaded", OpenXrSession.RefusedRead("SteamVR", Library(folder), refusal));
        }

        [Fact]
        public void ALibraryOutsideTheFolder_StaysLoaded()
        {
            string folder = NewFolder(), other = NewFolder();
            OpenXrSession.LoadLibraryOnce(Library(other), _ => (IntPtr)0x5105, out _);
            bool stopped = false, freed = false;
            OpenXrSession.ReleaseLibrariesUnder(folder, _ => { stopped = true; return true; }, _ => freed = true);
            Assert.False(stopped);
            Assert.False(freed);
            Assert.Equal((IntPtr)0x5105, OpenXrSession.LoadLibraryOnce(Library(other), _ => (IntPtr)0x5106,
                out OpenXrSession.LibraryRefusal refusal));
            Assert.Equal(OpenXrSession.LibraryRefusal.None, refusal);
        }

        [Fact]
        public void TheUninstall_StopsTheSourceThatHoldsTheLibrary_ThenFreesIt()
        {
            string folder = NewFolder();
            using var opened = new ManualResetEventSlim(false);
            var source = HoldingSource(Library(folder), opened);
            try
            {
                source.Start();
                Assert.True(opened.Wait(5000));
                int frees = 0;
                bool stoppedFirst = false;
                OpenXrSession.ReleaseLibrariesUnder(folder, OpenXrHeadPoseSource.StopHolders, _ =>
                {
                    frees++;
                    stoppedFirst = !source.Running && source.State == OpenXrSourceState.Stopped;
                });
                Assert.Equal(1, frees);
                Assert.True(stoppedFirst);
            }
            finally { source.Stop(); }
        }

        [Fact]
        public void ASourceThreadThatOutlivesItsJoin_KeepsTheLibraryLoaded()
        {
            string folder = NewFolder();
            using var opened = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var source = HoldingSource(Library(folder), opened, release);
            source.StopJoinMs = 20;
            try
            {
                source.Start();
                Assert.True(opened.Wait(5000));
                bool freed = false;
                OpenXrSession.ReleaseLibrariesUnder(folder, OpenXrHeadPoseSource.StopHolders, _ => freed = true);
                Assert.False(freed);
                OpenXrSession.LoadLibraryOnce(Library(folder), _ => (IntPtr)0x5109, out OpenXrSession.LibraryRefusal refusal);
                Assert.Equal(OpenXrSession.LibraryRefusal.StillHeld, refusal);
            }
            finally
            {
                release.Set();
                source.Stop();
            }
        }

        [Fact]
        public void ASourceOnAnotherRuntime_KeepsRunningThroughTheUninstall()
        {
            string steamVr = NewFolder(), other = NewFolder();
            using var openedSteamVr = new ManualResetEventSlim(false);
            using var openedOther = new ManualResetEventSlim(false);
            var onSteamVr = HoldingSource(Library(steamVr), openedSteamVr);
            var onOther = HoldingSource(Path.Combine(other, "runtime.dll"), openedOther);
            try
            {
                onSteamVr.Start();
                onOther.Start();
                Assert.True(openedSteamVr.Wait(5000));
                Assert.True(openedOther.Wait(5000));
                OpenXrSession.ReleaseLibrariesUnder(steamVr, OpenXrHeadPoseSource.StopHolders, _ => { });
                Assert.False(onSteamVr.Running);
                Assert.True(onOther.Running);
                Assert.NotEqual(OpenXrSourceState.Stopped, onOther.State);
            }
            finally
            {
                onSteamVr.Stop();
                onOther.Stop();
            }
        }

        [Fact]
        public void ThePublicEntry_StopsTheSourceOnTheLibrary_ThenFreesItsHandle()
        {
            string folder = NewFolder();
            using var opened = new ManualResetEventSlim(false);
            var source = HoldingSource(Library(folder), opened);
            var freed = new List<IntPtr>();
            bool stoppedFirst = false;
            var free = OpenXrHeadPoseSource.FreeLibrary;
            OpenXrHeadPoseSource.FreeLibrary = module =>
            {
                freed.Add(module);
                stoppedFirst = !source.Running && source.State == OpenXrSourceState.Stopped;
            };
            try
            {
                source.Start();
                Assert.True(opened.Wait(5000));
                OpenXrHeadPoseSource.ReleaseLibrariesUnder(folder);
                Assert.Equal(new[] { (IntPtr)0x5200 }, freed);
                Assert.True(stoppedFirst);
            }
            finally
            {
                OpenXrHeadPoseSource.FreeLibrary = free;
                source.Stop();
            }
        }

        [Fact]
        public void ASourceThatEnded_LetsGo_SoALaterSourceOnTheLibraryIsFreed()
        {
            string folder = NewFolder();
            using var openedFirst = new ManualResetEventSlim(false);
            using var openedNext = new ManualResetEventSlim(false);
            var first = HoldingSource(Library(folder), openedFirst);
            var next = HoldingSource(Library(folder), openedNext);
            try
            {
                first.Start();
                Assert.True(openedFirst.Wait(5000));
                first.Stop();
                next.Start();
                Assert.True(openedNext.Wait(5000));
                int frees = 0;
                OpenXrSession.ReleaseLibrariesUnder(folder, OpenXrHeadPoseSource.StopHolders, _ => frees++);
                Assert.Equal(1, frees);
                Assert.False(next.Running);
            }
            finally
            {
                first.Stop();
                next.Stop();
            }
        }

        [Fact]
        public void TheUninstallDelete_NoLongerMeetsAHeldLibrary()
        {
            string folder = NewFolder();
            string library = Library(folder);
            Directory.CreateDirectory(Path.GetDirectoryName(library));
            File.WriteAllText(library, "library");
            // The fake handle holds the file open with no delete sharing, the
            // way a loaded module holds its file.
            var held = new FileStream(library, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                OpenXrSession.LoadLibraryOnce(library, _ => (IntPtr)0x5107, out _);
                DriverInstaller.RemoveSteamVrFolder(folder,
                    dir => OpenXrSession.ReleaseLibrariesUnder(dir, _ => true, _ => held.Dispose()),
                    dir => Directory.Delete(dir, recursive: true),
                    Directory.Exists,
                    () => { });
                Assert.False(Directory.Exists(folder));
            }
            finally
            {
                held.Dispose();
                try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
