using System;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The second reader a device's header names, and the reason its layout
    /// gives when it has none (#600 item 32): each reason true for the device
    /// it is said of, and the header and the reader line agreeing.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class RecorderSecondReaderFactsTests
    {
        private const string BlissBoxPortGuid = "03000000d0160000040d000000000000";

        // ── A proven HIDAPI driver off USB ──

        /// <summary>A DualSense over Bluetooth is read by the driver the
        /// comparison proves, numbered as over USB. What stops the comparison
        /// is the bus. An unproven driver keeps the numbering reason, and so
        /// does a proven one read in the joystick layout.</summary>
        [Fact]
        public void AProvenSonyPad_OffUsb_SaysTheProofIsOfItsUsbReports()
        {
            var (cls, why) = RecorderReaderPlan.Classify("050057564c050000e60c000000006800", false, false, 0x054C, 0x0CE6, gamepadLayout: true);
            Assert.Equal(ReaderClass.None, cls);
            Assert.Equal("the comparison is proven on its USB reports only", why);
            Assert.Equal("SDL's driver numbers its controls its own way",
                RecorderReaderPlan.Classify("030000007e0500000920000000006800", false, false, 0x057E, 0x2009, true).Reason);
            Assert.Equal("SDL's driver numbers its controls its own way",
                RecorderReaderPlan.Classify("030000004c050000e60c000000006800", false, false, 0x054C, 0x0CE6, gamepadLayout: false).Reason);
            Assert.Equal(ReaderClass.Hidapi, RecorderReaderPlan.Classify("030000004c050000e60c000000006800", false, false, 0x054C, 0x0CE6, true).Class);
        }

        // ── A keyboard or mouse PadForge reads itself ──

        /// <summary>A replay's keyboard is a program device with no SDL GUID:
        /// PadForge reads it itself, and no Windows driver holds it. A
        /// keyboard Raw Input reads, and one saved and not connected now,
        /// keep Windows' line.</summary>
        [Fact]
        public void AProgramKeyboard_IsReadByPadForgeItself_AndARealOneIsWindowsAlone()
        {
            using var program = new ProgramDevice(Guid.NewGuid(), Guid.NewGuid(), "program://replay1", "Replayed Keyboard",
                new ProgramDeviceShape { DeviceType = InputDeviceType.Keyboard });
            var replayed = new UserDevice { InstanceGuid = Guid.NewGuid() };
            replayed.LoadFromExternalDevice(program);
            replayed.Device = program;
            Assert.True(replayed.IsKeyboard);
            Assert.Equal("none, PadForge reads it itself", RecorderReaders.SecondFact(replayed));

            var real = RecorderTestKit.Keyboard();
            real.Device = new SdlKeyboardWrapper();
            Assert.Equal("none, Windows opens it for itself alone", RecorderReaders.SecondFact(real));
            Assert.Equal("none, Windows opens it for itself alone", RecorderReaders.SecondFact(RecorderTestKit.Keyboard()));
        }

        // ── A Bliss-Box port ──

        private static UserDevice Port(SdlDeviceWrapper wrapper)
        {
            typeof(SdlDeviceWrapper).GetProperty(nameof(SdlDeviceWrapper.SdlGuid)).SetValue(wrapper, BlissBoxPortGuid);
            var port = RecorderTestKit.Pad("4Play Adapter", BlissBoxProtocol.VendorId, BlissBoxProtocol.PlayerOneProductId, InputDeviceType.Joystick);
            port.SdlGuid = BlissBoxPortGuid;
            port.Device = wrapper;
            return port;
        }

        /// <summary>A port SDL reads raw while Read Bliss-Box Adapters is on
        /// has PadForge's merge in its reading, so its header says it has no
        /// second reader, as its layout does. With the switch off nothing is
        /// merged, and the port is a DirectInput pad like any other.</summary>
        [Fact]
        public void ABlissBoxPortsHeader_SaysTheMergeOnlyWhileItRuns()
        {
            bool before = BlissBoxApi.Enabled;
            using var wrapper = new SdlDeviceWrapper();
            try
            {
                var port = Port(wrapper);
                BlissBoxApi.Enabled = true;
                Assert.Equal("none, PadForge merges its adapter's own state into its reading", RecorderReaders.SecondFact(port));
                BlissBoxApi.Enabled = false;
                Assert.Equal("rawinput", RecorderReaders.SecondFact(port));
            }
            finally { BlissBoxApi.Enabled = before; }
        }

        private static RecorderReaders.Facts FactsOf(UserDevice device, ISdlInputDevice live, string guid, bool gamepadLayout, bool gamepadOpened)
            => new(device, live, guid, device.DevicePath, device.VendorId, device.ProdId, gamepadLayout, gamepadOpened,
                false, false, 8, 16, 8, 1, 16, 7, false);

        /// <summary>The layout of a port: the merge's reason while the switch
        /// is on and the row is read raw, and with the switch off the port is
        /// asked about as any DirectInput pad is.</summary>
        [Fact]
        public void ABlissBoxPortsLayout_GivesTheMergeReasonOnlyWhileItRuns()
        {
            bool before = BlissBoxApi.Enabled;
            var collections = RecorderReaders.CollectionSeam;
            var mappings = RecorderReaders.MappingSeam;
            using var wrapper = new SdlDeviceWrapper();
            RecorderReaders.CollectionSeam = path => (IntPtr.Zero, 0);
            RecorderReaders.MappingSeam = id => string.Empty;
            try
            {
                var port = Port(wrapper);
                BlissBoxApi.Enabled = true;
                Assert.Equal("PadForge merges its adapter's own state into its reading",
                    RecorderReaders.WindowsReaders.Make(FactsOf(port, wrapper, BlissBoxPortGuid, false, false)).Reason);
                // Off, SDL maps the port as a gamepad with its own row.
                BlissBoxApi.Enabled = false;
                Assert.Equal("SDL has no DirectInput mapping for it",
                    RecorderReaders.WindowsReaders.Make(FactsOf(port, wrapper, BlissBoxPortGuid, true, true)).Reason);
            }
            finally
            {
                BlissBoxApi.Enabled = before;
                RecorderReaders.CollectionSeam = collections;
                RecorderReaders.MappingSeam = mappings;
            }
        }

        // ── A layout that waits on SDL's mapping ──

        /// <summary>While the engine does not answer, a DirectInput pad in
        /// the gamepad layout gets no layout, and Raw Input's devices are not
        /// walked for it at any of ten looks. A stick, which needs no
        /// mapping, is walked for once: the positive control.</summary>
        [Fact]
        public void ALayoutThatWaitsOnTheEngine_WalksNoDeviceWhileItDoesNotAnswer()
        {
            var collections = RecorderReaders.CollectionSeam;
            var mappings = RecorderReaders.MappingSeam;
            int walks = 0;
            RecorderReaders.CollectionSeam = path => { walks++; return (IntPtr.Zero, 0); };
            RecorderReaders.MappingSeam = id => RecorderReaders.WindowsReaders.NoAnswer;
            try
            {
                var pad = RecorderTestKit.Pad("DirectInput Pad", 0x0079, 0x0006);
                const string guid = "03000000790000000600000000000000";
                pad.SdlGuid = guid;
                for (int i = 0; i < 10; i++)
                    Assert.Null(RecorderReaders.WindowsReaders.Make(FactsOf(pad, null, guid, true, true)));
                Assert.Equal(0, walks);

                Assert.Equal("Raw Input lists no game controller at its path",
                    RecorderReaders.WindowsReaders.Make(FactsOf(pad, null, guid, false, false)).Reason);
                Assert.Equal(1, walks);
            }
            finally
            {
                RecorderReaders.CollectionSeam = collections;
                RecorderReaders.MappingSeam = mappings;
            }
        }
    }
}
