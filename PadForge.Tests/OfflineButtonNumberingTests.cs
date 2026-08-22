using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #344 (Xaklse): the same controller wore two different sets
    /// of button numbers depending on whether it was connected when PadForge
    /// launched.
    ///
    /// <para>An 8BitDo Ultimate 2 has fifteen buttons. Connected, they showed
    /// as 0-10 and 12-15, which is where SDL actually positions them in its
    /// fixed 22-slot gamepad space. Disconnected, they showed as 0-14, a
    /// dense list invented from the count. The count agreed in both states,
    /// so only the labels moved, and a user reading a number off the UI to
    /// find a physical button got a different answer each way.</para>
    ///
    /// <para>SDL's positions are the ones shown now, in both states. They are
    /// recorded on UserDevice while the device is connected and read back
    /// while it is not.</para>
    /// </summary>
    public class OfflineButtonNumberingTests
    {
        /// <summary>The reporter's device, as SDL positions it.</summary>
        private static readonly int[] Ultimate2 =
            { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 13, 14, 15 };

        private static UserDevice Offline(int[] recorded, int capCount)
            => new UserDevice
            {
                CapType = InputDeviceType.Gamepad,
                CapButtonCount = capCount,
                CapButtonIndices = recorded,
            };

        /// <summary>THE BUG. Offline, with positions recorded from a previous
        /// connection, the list is SDL's and not a dense count-derived one.</summary>
        [Fact]
        public void OfflineDevice_ReportsTheRecordedSdlPositions()
        {
            var ud = Offline(Ultimate2, capCount: 15);
            Assert.Equal(Ultimate2, InputService.ResolveButtonIndices(ud));
        }

        /// <summary>The specific complaint: the dense list is what it must NOT
        /// be. Stated separately so a regression that produces 0..14 fails
        /// with the reporter's own symptom rather than a generic mismatch.</summary>
        [Fact]
        public void OfflineDevice_DoesNotRenumberDensely()
        {
            var got = InputService.ResolveButtonIndices(Offline(Ultimate2, 15));
            Assert.Equal(15, got.Length);
            Assert.DoesNotContain(11, got);
            Assert.Contains(15, got);
            Assert.NotEqual(Enumerable.Range(0, 15).ToArray(), got);
        }

        /// <summary>A device PadForge has never seen online has no positions
        /// to report, so the dense range stays. Inventing a sparse list would
        /// be worse than a count-derived one.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData(new int[0])]
        public void NeverSeenOnline_KeepsTheDenseRange(int[] recorded)
        {
            var got = InputService.ResolveButtonIndices(Offline(recorded, 15));
            Assert.Equal(Enumerable.Range(0, 15).ToArray(), got);
        }

        /// <summary>Raw passthrough asks a different question, every native
        /// HID button, and keeps its dense raw range in both states.</summary>
        [Fact]
        public void RawPassthrough_IgnoresTheRecordedPositions()
        {
            var ud = Offline(Ultimate2, capCount: 15);
            ud.ForceRawJoystickMode = true;
            ud.RawButtonCount = 18;
            Assert.Equal(Enumerable.Range(0, 18).ToArray(),
                InputService.ResolveButtonIndices(ud));
        }

        /// <summary>Positions past the state array are dropped, the same cap
        /// the live path applies. A recorded list must not be trusted further
        /// than a live one.</summary>
        [Fact]
        public void RecordedPositionsPastTheCap_AreTrimmed()
        {
            int max = CustomInputState.MaxButtons;
            var ud = Offline(new[] { 0, 5, max - 1, max, max + 40 }, capCount: 5);
            Assert.Equal(new[] { 0, 5, max - 1 }, InputService.ResolveButtonIndices(ud));
        }

        /// <summary>The positions have to survive a restart, which is the
        /// whole point: they are read back precisely when no device is
        /// present to recompute them.</summary>
        [Fact]
        public void RecordedPositions_SurviveAnXmlRoundTrip()
        {
            var ud = new UserDevice
            {
                CapType = InputDeviceType.Gamepad,
                CapButtonCount = 15,
                CapButtonIndices = Ultimate2,
            };

            var ser = new XmlSerializer(typeof(UserDevice));
            string xml;
            using (var sw = new StringWriter()) { ser.Serialize(sw, ud); xml = sw.ToString(); }

            using var sr = new StringReader(xml);
            var back = (UserDevice)ser.Deserialize(sr);

            Assert.Equal(Ultimate2, back.CapButtonIndices);
        }

        /// <summary>An older config has no such element, and reading one must
        /// leave the field empty rather than throw or fabricate.</summary>
        [Fact]
        public void AConfigPredatingTheField_ReadsAsNoPositions()
        {
            var ser = new XmlSerializer(typeof(UserDevice));
            var ud = new UserDevice { CapType = InputDeviceType.Gamepad, CapButtonCount = 15 };
            string xml;
            using (var sw = new StringWriter()) { ser.Serialize(sw, ud); xml = sw.ToString(); }
            Assert.DoesNotContain("CapButtonIndices", xml);

            using var sr = new StringReader(xml);
            var back = (UserDevice)ser.Deserialize(sr);
            Assert.True(back.CapButtonIndices == null || back.CapButtonIndices.Length == 0);
            Assert.Equal(Enumerable.Range(0, 15).ToArray(),
                InputService.ResolveButtonIndices(back));
        }
    }
}
