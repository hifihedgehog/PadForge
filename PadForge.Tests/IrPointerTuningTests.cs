using System.IO;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pointer-tab tunables are per (device, slot) on PadSetting, not per
    /// device (issue #146 follow-up: moving the smoothing slider on one
    /// virtual controller must not change another VC sharing the same
    /// remote). Pins the persistence surface and the slot-scoped tuning
    /// application in ReadTunedIrPointer.
    /// </summary>
    public class IrPointerTuningTests
    {
        private static PadSetting RoundTrip(PadSetting ps)
        {
            var ser = new XmlSerializer(typeof(PadSetting));
            using var sw = new StringWriter();
            ser.Serialize(sw, ps);
            using var sr = new StringReader(sw.ToString());
            return (PadSetting)ser.Deserialize(sr);
        }

        [Fact]
        public void IrTunables_SurviveXmlRoundTrip()
        {
            var ps = new PadSetting { IrSensorBarPos = "2", IrSensorBarComp = "0.25", IrSmoothing = "0.6" };
            var rt = RoundTrip(ps);
            Assert.Equal("2", rt.IrSensorBarPos);
            Assert.Equal("0.25", rt.IrSensorBarComp);
            Assert.Equal("0.6", rt.IrSmoothing);
        }

        [Fact]
        public void IrTunables_DefaultToZero_OnLegacyXml()
        {
            const string legacy = "<?xml version=\"1.0\"?><PadSetting xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"></PadSetting>";
            var ser = new XmlSerializer(typeof(PadSetting));
            using var sr = new StringReader(legacy);
            var ps = (PadSetting)ser.Deserialize(sr);
            Assert.Equal("0", ps.IrSensorBarPos);
            Assert.Equal("0", ps.IrSensorBarComp);
            Assert.Equal("0", ps.IrSmoothing);
        }

        [Fact]
        public void IrTunables_ChangeTheContentChecksum()
        {
            var a = new PadSetting();
            var b = new PadSetting { IrSmoothing = "0.5" };
            var c = new PadSetting { IrSensorBarPos = "1", IrSensorBarComp = "0.1" };
            Assert.NotEqual(a.ComputeChecksum(), b.ComputeChecksum());
            Assert.NotEqual(a.ComputeChecksum(), c.ComputeChecksum());
            Assert.NotEqual(b.ComputeChecksum(), c.ComputeChecksum());
        }

        [Fact]
        public void Tuning_AppliesPerSlot_NotPerDevice()
        {
            // Two slots read the SAME device state through different Pointer-tab
            // settings: slot 0 has a bar offset, slot 1 has none. The whole
            // point of the follow-up: the slots must see different values.
            var prev = SourceCoercion.IrTuningProvider;
            try
            {
                SourceCoercion.IrTuningProvider = (dev, slot) =>
                    slot == 0 ? (0.3f, 0f) : (0f, 0f);

                var state = new CustomInputState();
                state.Ir.Y = 0.2f;
                state.Ir.Detected = true;
                var src = new MappingSource { Descriptor = "IR Pointer Y", DeviceGuid = "dev-a" };

                float slot0 = SourceCoercion.EvaluateForBipolarAxisTarget(state, src, slotIndex: 0);
                float slot1 = SourceCoercion.EvaluateForBipolarAxisTarget(state, src, slotIndex: 1);

                Assert.Equal(0.5f, slot0, precision: 5); // 0.2 + 0.3 offset
                Assert.Equal(0.2f, slot1, precision: 5); // untouched
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
            }
        }

        [Fact]
        public void Smoothing_IsPerSlot_AndResetsOnSightLoss()
        {
            var prev = SourceCoercion.IrTuningProvider;
            try
            {
                // Slot 0 smooths heavily; slot 1 is raw.
                SourceCoercion.IrTuningProvider = (dev, slot) =>
                    slot == 0 ? (0f, 0.5f) : (0f, 0f);

                var src = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "dev-b" };
                var s1 = new CustomInputState(); s1.Ir.X = 1.0f; s1.Ir.Detected = true;

                // First sample seeds the EMA (no prev), both slots read 1.0.
                Assert.Equal(1.0f, SourceCoercion.EvaluateForBipolarAxisTarget(s1, src, 0), precision: 5);
                Assert.Equal(1.0f, SourceCoercion.EvaluateForBipolarAxisTarget(s1, src, 1), precision: 5);

                // Aim jumps to 0: smoothed slot lags halfway, raw slot follows.
                var s2 = new CustomInputState(); s2.Ir.X = 0.0f; s2.Ir.Detected = true;
                Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(s2, src, 0), precision: 5);
                Assert.Equal(0.0f, SourceCoercion.EvaluateForBipolarAxisTarget(s2, src, 1), precision: 5);

                // Sight loss resets slot 0's EMA, so a re-acquire snaps.
                var lost = new CustomInputState(); lost.Ir.Detected = false;
                Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(lost, src, 0), precision: 5);
                var s3 = new CustomInputState(); s3.Ir.X = 1.0f; s3.Ir.Detected = true;
                Assert.Equal(1.0f, SourceCoercion.EvaluateForBipolarAxisTarget(s3, src, 0), precision: 5);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
            }
        }
    }
}
