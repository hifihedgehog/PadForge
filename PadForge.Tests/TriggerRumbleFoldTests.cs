using System.IO;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Trigger Rumble Fold (#271 item 2): the game's impulse-trigger
    /// channels max-folded into the body motors on devices without
    /// trigger motors. Covers the pure engine fold, the PadSetting
    /// persistence surface, and the checksum so a fold edit marks the
    /// profile dirty.
    /// </summary>
    public class TriggerRumbleFoldTests
    {
        // ── The pure fold ──

        [Fact]
        public void Fold_TriggerAboveMain_RaisesMainToTriggerLevel()
        {
            var v = new Vibration
            {
                LeftTriggerMotorSpeed = 40000,
                RightTriggerMotorSpeed = 50000,
            };
            ushort left = 10000, right = 20000;
            ForceFeedbackState.FoldTriggersIntoMains(v, ref left, ref right);
            Assert.Equal(40000, left);
            Assert.Equal(50000, right);
        }

        [Fact]
        public void Fold_TriggerBelowMain_LeavesMainUntouched()
        {
            var v = new Vibration
            {
                LeftTriggerMotorSpeed = 5000,
                RightTriggerMotorSpeed = 0,
            };
            ushort left = 30000, right = 60000;
            ForceFeedbackState.FoldTriggersIntoMains(v, ref left, ref right);
            Assert.Equal(30000, left);
            Assert.Equal(60000, right);
        }

        [Fact]
        public void Fold_SidesAreIndependent()
        {
            // Left trigger folds up, right trigger loses to the right main.
            var v = new Vibration
            {
                LeftTriggerMotorSpeed = 65535,
                RightTriggerMotorSpeed = 100,
            };
            ushort left = 0, right = 65535;
            ForceFeedbackState.FoldTriggersIntoMains(v, ref left, ref right);
            Assert.Equal(65535, left);
            Assert.Equal(65535, right);
        }

        [Fact]
        public void Fold_NullVibration_IsANoOp()
        {
            ushort left = 123, right = 456;
            ForceFeedbackState.FoldTriggersIntoMains(null, ref left, ref right);
            Assert.Equal(123, left);
            Assert.Equal(456, right);
        }

        [Fact]
        public void Fold_SilentTriggers_ZeroMainsStayZero()
        {
            var v = new Vibration();
            ushort left = 0, right = 0;
            ForceFeedbackState.FoldTriggersIntoMains(v, ref left, ref right);
            Assert.Equal(0, left);
            Assert.Equal(0, right);
        }

        // ── Persistence surface ──

        [Fact]
        public void PadSetting_TriggerRumbleFold_DefaultsOff()
        {
            Assert.Equal("0", new PadSetting().TriggerRumbleFold);
        }

        [Fact]
        public void PadSetting_TriggerRumbleFold_RoundTripsThroughXml()
        {
            var ps = new PadSetting { TriggerRumbleFold = "1" };
            var ser = new XmlSerializer(typeof(PadSetting));
            using var sw = new StringWriter();
            ser.Serialize(sw, ps);
            using var sr = new StringReader(sw.ToString());
            var back = (PadSetting)ser.Deserialize(sr);
            Assert.Equal("1", back.TriggerRumbleFold);
        }

        [Fact]
        public void PadSetting_TriggerRumbleFold_ChangesChecksum()
        {
            // The dirty-gate and dedup machinery ride the checksum; a fold
            // edit that doesn't move it would silently never persist.
            var off = new PadSetting { TriggerRumbleFold = "0" };
            var on = new PadSetting { TriggerRumbleFold = "1" };
            Assert.NotEqual(off.ComputeChecksum(), on.ComputeChecksum());
        }
    }
}
