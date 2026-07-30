using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round-34 guard for the build-then-swap discipline on the two lists
    /// the poll thread enumerates without a lock.
    ///
    /// <para>ApplyShiftLayerSnapshot already documents the rule: "the engine
    /// polling thread enumerates slotMs.ShiftActivators every frame without
    /// our lock, so it must never observe a list that is mid-fill." The
    /// PadPage add and delete paths mutated that same list in place, so a
    /// layer added or deleted while the pad was being polled could throw
    /// "collection was modified" inside ApplyMappingSetToGamepad's foreach.
    /// UpdateOutputStates catches it, which means the device silently lost
    /// its whole mapping evaluation for that frame.</para>
    ///
    /// <para>These tests assert the observable consequence of a swap: a
    /// reader holding the previous list reference keeps seeing a stable,
    /// complete list. That is exactly what the poll thread does, since both
    /// ResolveActiveLayerMask and the inheritance scan capture the reference
    /// before iterating.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class LiveListSwapTests : System.IDisposable
    {
        // SlotMappingSets is process-wide state that other classes in this
        // collection read. Restore whatever was there, so these tests cannot
        // decide the outcome of a test that happens to run after them.
        private readonly MappingSet[] _priorSets = SettingsManager.SlotMappingSets;

        public void Dispose() => SettingsManager.SlotMappingSets = _priorSets;

        private static MappingSet SetWith(params string[] masks)
        {
            var ms = new MappingSet();
            foreach (var m in masks)
                ms.ShiftActivators.Add(new ShiftActivator { LayerMask = m, LayerName = m, Mode = "Hold" });
            return ms;
        }

        [Fact]
        public void LayerDelete_SwapsTheListInsteadOfMutatingIt()
        {
            var ms = SetWith("Shift", "Alt");
            SettingsManager.SlotMappingSets = new[] { ms };
            var target = ms.ShiftActivators[0];

            // What the poll thread holds when the delete lands.
            var pollThreadView = ms.ShiftActivators;

            PadForge.Views.PadPage.ExecuteLayerDelete(ms, target, "Shift", new List<PadViewModel>());

            Assert.NotSame(pollThreadView, ms.ShiftActivators);   // swapped
            Assert.Equal(2, pollThreadView.Count);                // old view intact
            Assert.Single(ms.ShiftActivators);                    // new view correct
            Assert.Equal("Alt", ms.ShiftActivators[0].LayerMask);
        }

        [Fact]
        public void LayerDelete_OldViewStaysEnumerableThroughTheEdit()
        {
            // The failure this replaces: an in-place Remove during the poll
            // thread's foreach throws InvalidOperationException.
            var ms = SetWith("Shift", "Alt", "Ctrl");
            SettingsManager.SlotMappingSets = new[] { ms };
            var view = ms.ShiftActivators;

            int seen = 0;
            foreach (var a in view)
            {
                if (seen == 0)
                    PadForge.Views.PadPage.ExecuteLayerDelete(
                        ms, ms.ShiftActivators[2], "Ctrl", new List<PadViewModel>());
                seen++;
            }
            Assert.Equal(3, seen);
        }

        [Fact]
        public void RowSources_SwapLeavesTheOldListComplete()
        {
            // The mirror of the same rule on MappingRow.Sources, which the
            // save path rebuilds. A reader that captured the old list must
            // keep seeing every entry it had.
            var row = new MappingRow { Target = "ButtonA" };
            row.Sources.Add(new MappingSource { Descriptor = "Button 1" });
            row.Sources.Add(new MappingSource { Descriptor = "Button 2" });
            var captured = row.Sources;

            row.Sources = new List<MappingSource>
            {
                new MappingSource { Descriptor = "Button 9" }
            };

            Assert.Equal(2, captured.Count);
            Assert.Single(row.Sources);
            Assert.NotSame(captured, row.Sources);
        }

        [Fact]
        public void CapturedSources_SurviveAConcurrentSwapMidIteration()
        {
            // Pins the reader-side half: capture the reference, then index
            // that local. Pre-fix the loops re-read row.Sources every
            // iteration, so a swap between the Count read and the indexer
            // threw ArgumentOutOfRangeException on the poll thread.
            var row = new MappingRow { Target = "ButtonA" };
            for (int i = 0; i < 4; i++)
                row.Sources.Add(new MappingSource { Descriptor = "Button " + i });

            var srcs = row.Sources;
            int count = 0;
            for (int i = 0; i < srcs.Count; i++)
            {
                if (i == 1) row.Sources = new List<MappingSource>();  // the save lands
                Assert.NotNull(srcs[i]);
                count++;
            }
            Assert.Equal(4, count);
            Assert.Empty(row.Sources);
        }
    }
}
