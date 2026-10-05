using System;
using System.Collections.Generic;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Base-row index must not outlive the rows it was built from.
    ///
    /// <para>It invalidated on the mapping set's identity and its row COUNT
    /// only. The save and merge paths publish a rebuilt row list by reference
    /// assignment, so an equal-length republication kept the old rows
    /// resolving, and so did an in-place edit that kept the count.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class BaseRowCacheInvalidationTests : IDisposable
    {
        private readonly MappingSet[] saved = SettingsManager.SlotMappingSets;

        public void Dispose() => SettingsManager.SlotMappingSets = saved;

        private static MappingRow Row(string target, string descriptor) => new()
        {
            Target = target,
            LayerMask = "Base",
            Sources = new List<MappingSource> { new() { Descriptor = descriptor } },
        };

        private static MappingRow Find(MappingSet set, string target)
        {
            var m = typeof(InputManager).GetMethod("FindBaseRowForTarget",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.True(m != null, "FindBaseRowForTarget is gone");
            return (MappingRow)m.Invoke(null, new object[] { set, target });
        }

        /// <summary>An equal-length rebuilt list is a different list, and the
        /// index must follow it.</summary>
        [Fact]
        public void RepublishingAnEqualLengthRowListInvalidatesTheIndex()
        {
            var set = new MappingSet { Rows = new List<MappingRow> { Row("ButtonA", "Button 1") } };
            Assert.Equal("Button 1", Find(set, "ButtonA").Sources[0].Descriptor);

            // Same count, brand new list, as the save path publishes it.
            set.Rows = new List<MappingRow> { Row("ButtonA", "Button 7") };
            Assert.Equal("Button 7", Find(set, "ButtonA").Sources[0].Descriptor);
        }

        /// <summary>A wholesale row replacement retires the slot's index, so an
        /// in-place edit that keeps both the list and the count cannot keep
        /// resolving the old target.</summary>
        [Fact]
        public void APerSlotResetRetiresTheIndexForThatSlot()
        {
            var set = new MappingSet { Rows = new List<MappingRow> { Row("ButtonA", "Button 1") } };
            var sets = new MappingSet[InputManager.MaxPads];
            sets[2] = set;
            SettingsManager.SlotMappingSets = sets;

            Assert.NotNull(Find(set, "ButtonA"));

            // Retarget in place: same list instance, same count.
            set.Rows[0].Target = "ButtonB";
            InputManager.ResetSourceKindRuntimeForSlot(2);

            Assert.Null(Find(set, "ButtonA"));
            Assert.NotNull(Find(set, "ButtonB"));
        }

        /// <summary>A row removed and another appended on the same list keeps
        /// the count and the reference. The index handed back the removed row.
        /// Removing rows in place (device cleanup) and appending in place (the
        /// motion backfill, the grid push) are both live edits.</summary>
        [Fact]
        public void AnInPlaceRemoveAndAppendAtTheSameCountInvalidatesTheIndex()
        {
            var set = new MappingSet
            {
                Rows = new List<MappingRow> { Row("ButtonA", "Button 1"), Row("ButtonB", "Button 2") },
            };
            Assert.Equal("Button 1", Find(set, "ButtonA").Sources[0].Descriptor);

            set.Rows.RemoveAll(r => r.Target == "ButtonA");
            set.Rows.Add(Row("ButtonA", "Button 7"));

            Assert.Equal("Button 7", Find(set, "ButtonA").Sources[0].Descriptor);
        }

        /// <summary>The same edit must also end a cached miss: a target that
        /// had no row gains one while another row leaves.</summary>
        [Fact]
        public void AnInPlaceRemoveAndAppendAtTheSameCountEndsACachedMiss()
        {
            var set = new MappingSet
            {
                Rows = new List<MappingRow> { Row("ButtonA", "Button 1"), Row("ButtonB", "Button 2") },
            };
            Assert.Null(Find(set, "ButtonX"));

            set.Rows.RemoveAll(r => r.Target == "ButtonB");
            set.Rows.Add(Row("ButtonX", "Button 3"));

            Assert.Equal("Button 3", Find(set, "ButtonX")?.Sources[0].Descriptor);
            Assert.Null(Find(set, "ButtonB"));
        }

        /// <summary>An edit that changes neither the count, the list nor its
        /// last row, here a retarget in place with no per-slot reset, is picked
        /// up by the periodic rebuild, the bound the pressure rows' rescan
        /// keeps too.</summary>
        [Fact]
        public void AnEditNoCheckSees_IsPickedUpByThePeriodicRebuild()
        {
            var set = new MappingSet { Rows = new List<MappingRow> { Row("ButtonA", "Button 1") } };
            Assert.NotNull(Find(set, "ButtonA"));

            set.Rows[0].Target = "ButtonB";
            System.Threading.Thread.Sleep(300);

            Assert.Null(Find(set, "ButtonA"));
            Assert.NotNull(Find(set, "ButtonB"));
        }
    }
}
