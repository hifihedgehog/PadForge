using System.Linq;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #252's mandated persisted descriptor round-trip, the guard the
    /// feature shipped without. The issue states it as a requirement, not a
    /// nicety: "A persisted descriptor round-trip is mandatory for any new
    /// family here, for reasons #146 taught us the hard way: pick in the UI,
    /// save, load, migrate, evaluate, and assert the descriptor survives
    /// byte-identical."
    ///
    /// <para>The shipped coverage was predicate-level (exact-match and
    /// disjointness) and WIRE-level (the Remote Link codec tail). Neither
    /// crosses the settings-XML path, and that is precisely where #146's
    /// corruption lived: a normalizer between a correct producer and a
    /// correct consumer ate the leading character of "IR Pointer X" and
    /// persisted "R Pointer X" + Invert, so the camera tracked perfectly
    /// while the mapping matched nothing.</para>
    ///
    /// <para>The aux family is not I/H-prefixed, so it needs no exemption
    /// predicate. What it does need is proof that no normalizer on the
    /// persisted path rewrites it, which is what these pin.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class GyroAuxDescriptorPersistenceTests
    {
        // ─── The legacy migrator: the layer that corrupted #146 ───

        [Theory]
        [InlineData(SourceCoercion.GyroAuxPitchDescriptor)]
        [InlineData(SourceCoercion.GyroAuxYawDescriptor)]
        [InlineData(SourceCoercion.GyroAuxRollDescriptor)]
        public void GyroAuxRateDescriptors_SurviveBuildFromLegacy_ByteIdentical(string descriptor)
        {
            var ps = new PadSetting { LeftThumbAxisX = descriptor };
            var ms = MappingSetMigrator.BuildFromLegacy(
                0, new[] { ("11111111-1111-1111-1111-111111111111", ps) });

            var row = ms.Rows.FirstOrDefault(r => r.Target == "LeftThumbAxisX");
            Assert.NotNull(row);
            var src = Assert.Single(row.Sources);

            // Byte-identical: no prefix eaten, no case folded, no token dropped.
            Assert.Equal(descriptor, src.Descriptor);
            // And no phantom modifier synthesized out of the name itself.
            Assert.False(src.Invert);
            Assert.False(src.HalfAxis);
        }

        // ─── The settings XML: save + load ───

        [Theory]
        [InlineData(SourceCoercion.GyroAuxPitchDescriptor)]
        [InlineData(SourceCoercion.GyroAuxYawDescriptor)]
        [InlineData(SourceCoercion.GyroAuxRollDescriptor)]
        [InlineData(MappingSetMigrator.MotionGyroAuxSourceDescriptor)]
        [InlineData(MappingSetMigrator.MotionAccelAuxSourceDescriptor)]
        public void AuxDescriptors_SurviveTheMappingSetXmlRoundTrip_ByteIdentical(string descriptor)
        {
            var set = new MappingSet();
            var row = new MappingRow { Target = "MotionGyro" };
            row.Sources.Add(new MappingSource { Descriptor = descriptor });
            set.Rows.Add(row);

            var ser = new System.Xml.Serialization.XmlSerializer(typeof(MappingSet));
            using var mem = new System.IO.MemoryStream();
            ser.Serialize(mem, set);
            mem.Position = 0;
            var back = (MappingSet)ser.Deserialize(mem);

            var backSrc = Assert.Single(Assert.Single(back.Rows).Sources);
            Assert.Equal(descriptor, backSrc.Descriptor);
        }

        // ─── Evaluate: the descriptor still classifies as its own family ───

        [Fact]
        public void AuxDescriptors_StillClassifyAsAux_AfterThePersistedRoundTrip()
        {
            // The "evaluate" leg of the mandate. A descriptor can survive
            // storage byte-identical and still be misrouted downstream if a
            // classifier claims it, which is the half of #146 that made the
            // corruption fatal rather than cosmetic.
            var set = new MappingSet();
            var row = new MappingRow { Target = "LeftThumbAxisX" };
            row.Sources.Add(new MappingSource
            {
                Descriptor = SourceCoercion.GyroAuxPitchDescriptor,
            });
            set.Rows.Add(row);

            var ser = new System.Xml.Serialization.XmlSerializer(typeof(MappingSet));
            using var mem = new System.IO.MemoryStream();
            ser.Serialize(mem, set);
            mem.Position = 0;
            var back = (MappingSet)ser.Deserialize(mem);

            var d = Assert.Single(Assert.Single(back.Rows).Sources).Descriptor;

            // Reads as the aux family, and NOT as the primary gyro.
            Assert.True(SourceCoercion.IsGyroAuxDescriptor(d));
            Assert.False(MappingSetMigrator.IsMotionGyroAuxDescriptor(d));
        }
    }
}
