using System.Collections.Generic;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-26 round eighteen.
    ///
    /// <para>NO DEFECT FOUND in MappingSetMigrator. NormalizeRawToken is
    /// self-terminating by construction (it gates on a leading "Extended"
    /// and emits "Raw...", which cannot re-match), and
    /// EnsureMotionRowForSensor collects the already-present device guids
    /// case-insensitively before adding any, so a second pass contributes
    /// nothing.</para>
    ///
    /// <para>What the round found is that IDEMPOTENCE IS ASSERTED IN A
    /// DOCSTRING AND NOWHERE ELSE. NormalizeRawSurfaceTargets carried one
    /// test reference, EnsureMotionRows three, and the repo contained no
    /// idempotency test for the migrator at all, though it has one for
    /// stick-boundary convexify, so the habit exists.</para>
    ///
    /// <para>Why that gap is worth closing on this file specifically: the
    /// migrator runs at EVERY lane a persisted MappingSet enters the
    /// process (settings load, profile apply, legacy merge) and the result
    /// is saved back. A future rewrite rule that is not self-terminating
    /// would therefore corrode every user's profile a little on every
    /// load, persistently and silently. That is precisely how the Wii IR
    /// pointer shipped dead: a legacy prefix migrator rewrote "IR Pointer
    /// X" into Invert + "R Pointer X" and PERSISTED it, and five rounds of
    /// verification against the driver never looked at the normalizer in
    /// between.</para></summary>
    public class AuditJuly26RoundEighteenTests
    {
        // ── NormalizeRawToken ────────────────────────────────────────

        [Theory]
        [InlineData("ExtendedAxis3", "RawAxis3")]
        [InlineData("ExtendedBtn12", "RawBtn12")]
        [InlineData("ExtendedPov0", "RawPov0")]
        [InlineData("ExtendedStickLX", "RawStickLX")]
        [InlineData("ExtendedTriggerL", "RawTriggerL")]
        public void LegacyRawTokens_RewriteToRawGrammar(string legacy, string expected)
        {
            Assert.Equal(expected, MappingSetMigrator.NormalizeRawToken(legacy));
        }

        /// <summary>THE PROPERTY. Re-running the rewrite must be a no-op,
        /// because this runs on every load and the result is saved back.</summary>
        [Theory]
        [InlineData("ExtendedAxis3")]
        [InlineData("ExtendedBtn12")]
        [InlineData("ExtendedStickLX")]
        [InlineData("RawAxis3")]
        [InlineData("ButtonA")]
        [InlineData("")]
        [InlineData(null)]
        public void NormalizeRawToken_IsIdempotent(string token)
        {
            string once = MappingSetMigrator.NormalizeRawToken(token);
            string twice = MappingSetMigrator.NormalizeRawToken(once);
            Assert.Equal(once, twice);
        }

        /// <summary>An already-migrated token is left strictly alone, so a
        /// profile saved after one upgrade is stable forever.</summary>
        [Fact]
        public void AlreadyMigratedTokens_AreUntouched()
        {
            Assert.Equal("RawAxis3", MappingSetMigrator.NormalizeRawToken("RawAxis3"));
            Assert.Equal("RawBtn0", MappingSetMigrator.NormalizeRawToken("RawBtn0"));
        }

        /// <summary>A token that merely begins with "Extended" but is not a
        /// raw-surface token must survive intact. The rewrite is gated on
        /// the SUFFIX grammar, not on the prefix alone, which is the guard
        /// that keeps this from becoming another IR-Pointer-style
        /// over-eager strip.</summary>
        [Theory]
        [InlineData("Extended")]
        [InlineData("ExtendedFoo")]
        [InlineData("ExtendedGyro")]
        public void ExtendedPrefixedNonRawTokens_AreNotRewritten(string token)
        {
            Assert.Equal(token, MappingSetMigrator.NormalizeRawToken(token));
        }

        // ── NormalizeRawSurfaceTargets ───────────────────────────────

        /// <summary>The whole-set walk carries the same property. Its
        /// docstring claims idempotence; this is the only thing that
        /// checks it.</summary>
        [Fact]
        public void NormalizeRawSurfaceTargets_IsIdempotent()
        {
            var ms = new MappingSet
            {
                Rows = new List<MappingRow>
                {
                    new MappingRow { Target = "ExtendedAxis0" },
                    new MappingRow { Target = "RawBtn1" },
                    new MappingRow { Target = "ButtonA" },
                    new MappingRow { Target = "ExtendedTriggerR" },
                },
            };

            MappingSetMigrator.NormalizeRawSurfaceTargets(ms);
            var afterFirst = ms.Rows.ConvertAll(r => r.Target);

            MappingSetMigrator.NormalizeRawSurfaceTargets(ms);
            var afterSecond = ms.Rows.ConvertAll(r => r.Target);

            Assert.Equal(new[] { "RawAxis0", "RawBtn1", "ButtonA", "RawTriggerR" }, afterFirst);
            Assert.Equal(afterFirst, afterSecond);
        }

        /// <summary>Null and empty targets must not throw. A persisted set
        /// can carry a blank row, and this runs on the load path where a
        /// throw would take the whole profile down.</summary>
        [Fact]
        public void NormalizeRawSurfaceTargets_ToleratesNullsAndBlanks()
        {
            var ms = new MappingSet
            {
                Rows = new List<MappingRow>
                {
                    null,
                    new MappingRow { Target = null },
                    new MappingRow { Target = "" },
                    new MappingRow { Target = "ExtendedAxis1" },
                },
            };

            MappingSetMigrator.NormalizeRawSurfaceTargets(ms);

            Assert.Equal("RawAxis1", ms.Rows[3].Target);
            MappingSetMigrator.NormalizeRawSurfaceTargets(null);   // must not throw
            MappingSetMigrator.NormalizeRawSurfaceTargets(new MappingSet());
        }

        // ── EnsureMotionRows ─────────────────────────────────────────

        private static MappingSet EmptySet() =>
            new MappingSet { Rows = new List<MappingRow>() };

        private static IReadOnlyList<(string DeviceGuid, bool HasGyro, bool HasAccel)> OneImu(string guid)
            => new[] { (guid, true, true) };

        /// <summary>THE PROPERTY, for the row-adding half. Re-applying a
        /// profile must not stack a second identical source onto the motion
        /// row, which is what an unguarded "ensure" would do on every apply.</summary>
        [Fact]
        public void EnsureMotionRows_IsIdempotent()
        {
            var ms = EmptySet();
            var devices = OneImu("{AAAA-BBBB}");

            MappingSetMigrator.EnsureMotionRows(ms, slotType: 5, devices);
            int rowsAfterFirst = ms.Rows.Count;
            var gyro = ms.Rows.Find(r => r.Target == MappingSetMigrator.MotionGyroTarget);
            Assert.NotNull(gyro);
            int sourcesAfterFirst = gyro.Sources.Count;

            MappingSetMigrator.EnsureMotionRows(ms, slotType: 5, devices);

            Assert.Equal(rowsAfterFirst, ms.Rows.Count);
            Assert.Equal(sourcesAfterFirst, gyro.Sources.Count);
        }

        /// <summary>Guid case must not defeat the dedup: XML round-trips
        /// change case, so a case-sensitive check would add a duplicate
        /// source the first time a profile came back off disk.</summary>
        [Fact]
        public void EnsureMotionRows_DedupesAcrossGuidCase()
        {
            var ms = EmptySet();
            MappingSetMigrator.EnsureMotionRows(ms, 5, OneImu("{aaaa-bbbb}"));
            var gyro = ms.Rows.Find(r => r.Target == MappingSetMigrator.MotionGyroTarget);
            int before = gyro.Sources.Count;

            MappingSetMigrator.EnsureMotionRows(ms, 5, OneImu("{AAAA-BBBB}"));

            Assert.Equal(before, gyro.Sources.Count);
        }

        /// <summary>Non-motion slot families are left alone entirely, so an
        /// Xbox or keyboard slot never sprouts a motion row it cannot
        /// serve.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(4)]
        public void EnsureMotionRows_SkipsNonMotionSlotTypes(int slotType)
        {
            var ms = EmptySet();
            MappingSetMigrator.EnsureMotionRows(ms, slotType, OneImu("{AAAA-BBBB}"));
            Assert.Empty(ms.Rows);
        }

        /// <summary>A device without the sensor contributes no source, so a
        /// gyro-less pad never gets a dead Motion Gyro binding.</summary>
        [Fact]
        public void EnsureMotionRows_SkipsDevicesLackingTheSensor()
        {
            var ms = EmptySet();
            var gyroOnly = new[] { ("{CCCC}", true, false) };

            MappingSetMigrator.EnsureMotionRows(ms, 5, gyroOnly);

            var accel = ms.Rows.Find(r => r.Target == MappingSetMigrator.MotionAccelTarget);
            Assert.True(accel == null || accel.Sources == null || accel.Sources.Count == 0);
        }
    }
}
