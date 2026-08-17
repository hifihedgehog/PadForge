using System;
using System.IO;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    // Locks the yaw/roll invert split (issue #321, discussion #318 by
    // vlue-c): independent flags per axis, the Horizontal blend following
    // its dominant component, byte-identical defaults, and the legacy
    // migration where a profile saved under the combined toggle comes out
    // with BOTH axes inverted.
    [Collection("SettingsManagerStatics")]
    public class GyroInvertSplitTests
    {
        private const string Dev = "11112222-3333-4444-5555-666677778888";

        private static float Read(string descriptor, bool invYaw, bool invRoll,
            float yawRate, float rollRate)
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            try
            {
                SourceCoercion.AimEngageStateProvider = null;
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.GyroTuningProvider = (g, s) => new SourceCoercion.GyroTuning
                {
                    SensH = 1f, SensV = 1f, OutputCurve = "Linear",
                    InvertYaw = invYaw, InvertRoll = invRoll,
                };
                var st = new CustomInputState();
                st.Gyro[1] = yawRate;
                st.Gyro[2] = rollRate;
                var src = new MappingSource { Kind = "Direct", DeviceGuid = Dev, Descriptor = descriptor };
                return SourceEvaluator.EvaluateForBipolarAxisTarget(
                    st, src, 0, "LeftThumbAxisX", 0, null, 0.016, Dev);
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.AimEngageStateProvider = oldEngage;
                SourceCoercion.GyroBiasProvider = oldBias;
            }
        }

        [Fact]
        public void Defaults_AreByteIdenticalToTheCombinedEra()
        {
            // Both flags off must reproduce the pre-split relationship
            // exactly: roll enters the yaw lane NEGATED relative to yaw
            // (the deliberate agreement the Local blend needs). The
            // absolute deflection sign is the engine's own convention
            // and not this test's business.
            float yawOnly = Read("Gyro Yaw", false, false, 1.0f, 0f);
            float rollOnly = Read("Gyro Roll", false, false, 0f, 1.0f);
            Assert.NotEqual(0f, yawOnly);
            Assert.Equal(yawOnly, -rollOnly, 3);
        }

        [Fact]
        public void YawFlag_NeverFlipsARollRow_AndViceVersa()
        {
            float baseYaw = Read("Gyro Yaw", false, false, 1.0f, 0f);
            float baseRoll = Read("Gyro Roll", false, false, 0f, 1.0f);

            // The yaw flag flips only the yaw row.
            Assert.Equal(-baseYaw, Read("Gyro Yaw", true, false, 1.0f, 0f), 3);
            Assert.Equal(baseRoll, Read("Gyro Roll", true, false, 0f, 1.0f), 3);

            // The roll flag flips only the roll row.
            Assert.Equal(baseYaw, Read("Gyro Yaw", false, true, 1.0f, 0f), 3);
            Assert.Equal(-baseRoll, Read("Gyro Roll", false, true, 0f, 1.0f), 3);
        }

        [Fact]
        public void Horizontal_FollowsTheDominantComponentsFlag()
        {
            // Yaw dominant: only the yaw flag matters.
            float yawDom = Read("Gyro Horizontal", false, false, 1.0f, 0.2f);
            Assert.Equal(-yawDom, Read("Gyro Horizontal", true, false, 1.0f, 0.2f), 3);
            Assert.Equal(yawDom, Read("Gyro Horizontal", false, true, 1.0f, 0.2f), 3);

            // Roll dominant: only the roll flag matters.
            float rollDom = Read("Gyro Horizontal", false, false, 0.2f, 1.0f);
            Assert.Equal(-rollDom, Read("Gyro Horizontal", false, true, 0.2f, 1.0f), 3);
            Assert.Equal(rollDom, Read("Gyro Horizontal", true, false, 0.2f, 1.0f), 3);
        }

        [Fact]
        public void LegacyProfile_CombinedInvertInheritsOntoRoll()
        {
            // A profile saved before the split carries only the
            // GyroInvertYaw element. The roll sentinel (empty) must
            // resolve to the yaw value, and an authored value must win.
            var ser = new XmlSerializer(typeof(PadSetting));
            const string legacyXml =
                "<PadSetting><GyroInvertYaw>1</GyroInvertYaw></PadSetting>";
            using (var r = new StringReader(legacyXml))
            {
                var ps = (PadSetting)ser.Deserialize(r);
                Assert.Equal("1", ps.GyroInvertYaw);
                Assert.Equal("", ps.GyroInvertRoll);
                Assert.Equal("1", ps.GyroInvertRollEffective);
            }
            const string authoredXml =
                "<PadSetting><GyroInvertYaw>1</GyroInvertYaw><GyroInvertRoll>0</GyroInvertRoll></PadSetting>";
            using (var r = new StringReader(authoredXml))
            {
                var ps = (PadSetting)ser.Deserialize(r);
                Assert.Equal("0", ps.GyroInvertRollEffective);
            }
        }
    }
}
