using System.Collections.Generic;
using System.Linq;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Motion rows follow shift layers (owner decision, 2026-07-26).
    ///
    /// <para>The engine resolve now prefers the engaged layer's motion row,
    /// falls back to Base, and finally to any row naming the target, so the
    /// change is a strict preference re-ordering and motion can never go dark
    /// because a layer engaged.</para>
    ///
    /// <para>These lock the half of that change with a pure-data seam: the
    /// load-time backfill. It used to find the motion row LAYER-BLIND, so on a
    /// slot that already had a shift-layer motion row it appended the newly
    /// assigned device's sources there and never created a Base row. With the
    /// resolve now layer-aware that would have cost the slot its motion
    /// everywhere except inside that one layer.</para></summary>
    public class MotionLayerTests
    {
        private const string GyroDev = "11111111-1111-1111-1111-111111111111";

        private static MappingSet SetWithLayerMotionRow()
        {
            var ms = new MappingSet();
            // A motion row that exists ONLY on a shift layer, placed first so a
            // layer-blind find would take it.
            ms.Rows.Add(new MappingRow
            {
                Target = MappingSetMigrator.MotionGyroTarget,
                LayerMask = "Shift1",
                Sources = new List<MappingSource>(),
            });
            return ms;
        }

        private static IReadOnlyList<(string DeviceGuid, bool HasGyro, bool HasAccel)> OneGyroPad()
            => new[] { (GyroDev, true, true) };

        /// <summary>THE TRAP. A pre-existing shift-layer motion row must not
        /// capture the backfill; the slot still needs its Base row.</summary>
        [Fact]
        public void BackfillCreatesTheBaseRowEvenWhenALayerRowExistsFirst()
        {
            var ms = SetWithLayerMotionRow();

            // slotType 1 = PlayStation, one of the two families that get motion rows.
            MappingSetMigrator.EnsureMotionRows(ms, 1, OneGyroPad());

            var gyroRows = ms.Rows
                .Where(r => r.Target == MappingSetMigrator.MotionGyroTarget)
                .ToList();

            Assert.Contains(gyroRows, r => (r.LayerMask ?? "Base") == "Base");

            var baseRow = gyroRows.First(r => (r.LayerMask ?? "Base") == "Base");
            Assert.NotNull(baseRow.Sources);
            Assert.Contains(baseRow.Sources, s => s.DeviceGuid == GyroDev);
        }

        /// <summary>And the layer row must be left alone: the backfill is not
        /// entitled to stuff the slot's devices into someone's shift layer.</summary>
        [Fact]
        public void BackfillDoesNotWriteIntoTheLayerRow()
        {
            var ms = SetWithLayerMotionRow();

            MappingSetMigrator.EnsureMotionRows(ms, 1, OneGyroPad());

            var layerRow = ms.Rows.First(r =>
                r.Target == MappingSetMigrator.MotionGyroTarget && r.LayerMask == "Shift1");
            Assert.Empty(layerRow.Sources);
        }

        /// <summary>A null LayerMask means Base. Hand-edited XML and imported
        /// profiles deliver it, and treating it as a layer would make the
        /// backfill create a duplicate Base row beside the real one.</summary>
        [Fact]
        public void NullLayerMaskCountsAsBase()
        {
            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow
            {
                Target = MappingSetMigrator.MotionGyroTarget,
                LayerMask = null,
                Sources = new List<MappingSource>(),
            });

            MappingSetMigrator.EnsureMotionRows(ms, 1, OneGyroPad());

            var gyroRows = ms.Rows
                .Where(r => r.Target == MappingSetMigrator.MotionGyroTarget)
                .ToList();
            Assert.Single(gyroRows);
            Assert.Contains(gyroRows[0].Sources, s => s.DeviceGuid == GyroDev);
        }

        /// <summary>The ordinary case still works: no rows at all yields a
        /// Base row carrying the device.</summary>
        [Fact]
        public void BackfillFromEmptyCreatesBase()
        {
            var ms = new MappingSet();

            MappingSetMigrator.EnsureMotionRows(ms, 1, OneGyroPad());

            var gyroRow = ms.Rows.FirstOrDefault(r => r.Target == MappingSetMigrator.MotionGyroTarget);
            Assert.NotNull(gyroRow);
            Assert.Equal("Base", gyroRow.LayerMask ?? "Base");
            Assert.Contains(gyroRow.Sources, s => s.DeviceGuid == GyroDev);
        }
    }
}
