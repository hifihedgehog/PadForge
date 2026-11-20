using System;
using PadForge.Common.Input;
using PadForge.Engine.RemoteLink;
using PadForge.Engine.Steering;
using Xunit;

namespace PadForge.Tests
{
    [Collection("AudioProfileLifecycle")]
    public sealed class PeerWheelFrameTests
    {
        /// <summary>The poll ships a peer's wheel frame on every pass, and a
        /// frame the same as the last one sent goes no further than the
        /// compare. Making that compare built a list and an array on every
        /// pass, on the poll thread, and Vehicle mode's centering tail moves
        /// on most passes.</summary>
        [Fact]
        public void AnUnchangedPeerWheelFrameAllocatesNothing()
        {
            string path = "peer://wheel-frame/" + Guid.NewGuid().ToString("N");
            var sender = RemoteLinkOutputRouter.SendOutput;
            int sent = 0;
            var tail = OutputEffectCodec.CenteringTail.From(new CenteringFrame
            {
                Level = 3000, SpringCoefficient = 5000, SpringSaturation = 2000,
                DamperCoefficient = 4000, DamperSaturation = 6000, FrictionSaturation = 1000,
            });
            bool Ship() => RemoteLinkOutputRouter.ShipWheel(path, false, true, 1000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100,
                900, 0, false, centering: tail);
            try
            {
                RemoteLinkOutputRouter.Register(path, "wheel-frame", 0);
                RemoteLinkOutputRouter.SendOutput = (_, _, _) => sent++;
                Assert.True(Ship());
                Assert.Equal(1, sent);
                Assert.All(AllocationWindow.Clean(() => Ship(), 100), bytes => Assert.Equal(0, bytes));
                Assert.Equal(1, sent);
                // A changed frame still goes out, and decodes as it was written.
                Assert.True(RemoteLinkOutputRouter.ShipWheel(path, false, true, 1200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100,
                    900, 0, false, centering: tail));
                Assert.Equal(2, sent);
            }
            finally
            {
                RemoteLinkOutputRouter.Unregister(path);
                RemoteLinkOutputRouter.SendOutput = sender;
            }
        }

        /// <summary>The frame written into a buffer is the frame the array
        /// form gives, byte for byte, with and without the centering
        /// tail.</summary>
        [Fact]
        public void TheWheelFrameIsTheSameWrittenIntoABuffer()
        {
            var tail = OutputEffectCodec.CenteringTail.From(new CenteringFrame { Level = -3000, SpringCoefficient = 7000, SpringSaturation = 9000 });
            Span<byte> buffer = stackalloc byte[OutputEffectCodec.WheelFrameMax];
            foreach (var centering in new[] { default, tail })
            {
                byte[] array = OutputEffectCodec.EncodeWheel(true, false, -1234, 567, 0x12345678, 8, 100, 5000, -5000, 10, 20,
                    9000, 8000, 77, 900, 0x1F, true, true, centering);
                int length = OutputEffectCodec.EncodeWheel(buffer, true, false, -1234, 567, 0x12345678, 8, 100, 5000, -5000, 10, 20,
                    9000, 8000, 77, 900, 0x1F, true, true, centering);
                Assert.Equal(array, buffer[..length].ToArray());
                Assert.True(OutputEffectCodec.TryDecode(array, out var decoded));
                Assert.Equal(-1234, decoded.Wheel.Force);
                Assert.Equal(centering.Level, decoded.Wheel.Centering.Level);
            }
        }
    }
}
