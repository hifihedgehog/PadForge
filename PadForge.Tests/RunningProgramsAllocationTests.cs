using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Matching the running processes against every vendor program
    /// allocates nothing per process.
    ///
    /// <para>Each match of a program to a process made an enumerator, some
    /// 7,000 for one process snapshot, and the presence read takes a snapshot
    /// whenever the list of processes moves (measured 2026-11-16).</para>
    /// </summary>
    public class RunningProgramsAllocationTests
    {
        [Fact]
        public void MatchingRunningProgramsAllocatesNothingPerProcess()
        {
            var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 300; i++) images.Add("process" + i);
            RunningPrograms.Of(images, VendorPrograms.All);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var found = RunningPrograms.Of(images, VendorPrograms.All);
            long spent = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Same(RunningPrograms.None, found);
            Assert.True(spent < 1024, $"{spent} bytes for one match of {images.Count} processes");
        }

        /// <summary>A program still runs when a process of its name runs,
        /// and the match keeps the order of the known programs.</summary>
        [Fact]
        public void TheMatchStillFindsWhatRuns()
        {
            var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer", "lghub_agent", "Razer Synapse Service" };
            var found = RunningPrograms.Of(images, VendorPrograms.All);
            Assert.Contains(VendorPrograms.LogitechGHub, found.Programs);
            var list = new List<VendorProgram>(found.Programs);
            for (int i = 1; i < list.Count; i++)
                Assert.True(IndexOf(list[i - 1]) < IndexOf(list[i]));

            static int IndexOf(VendorProgram program)
            {
                for (int i = 0; i < VendorPrograms.All.Count; i++)
                    if (ReferenceEquals(VendorPrograms.All[i], program)) return i;
                return -1;
            }
        }
    }
}
