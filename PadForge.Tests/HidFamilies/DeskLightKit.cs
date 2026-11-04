using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>What every HID family of the desk-light lane shares (#557):
    /// a model for a family's one registration, and a session's calls.</summary>
    internal static class DeskLightKit
    {
        public static HidLightingModel ModelOf(IHidLightingFamily family, string folder, ushort output = 1024, ushort feature = 65)
            => family.Match(CollectionFor(OpenRgbRegistrations.Of(folder).First(r => r.Transport == OpenRgbTransport.Hid),
                output: output, feature: feature, input: 65));

        /// <summary>Begins a session on a fresh channel and paints one
        /// picture.</summary>
        public static (FakeReportChannel Io, HidLightingSession Session) Painted(HidLightingModel model, int[] colors)
        {
            var io = new FakeReportChannel { OutputReportLength = 1024, FeatureReportLength = 65, InputReportLength = 65 };
            var session = model.Begin(io, _ => { });
            Assert.NotNull(session);
            Assert.Empty(io.Calls);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, colors));
            return (io, session);
        }

        /// <summary>A session that has nothing to hand back sends nothing on
        /// its hand-back and names no crash frames.</summary>
        public static void HandsBackNothing(FakeReportChannel io, HidLightingSession session)
        {
            int before = io.Calls.Count;
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(before, io.Calls.Count);
            Assert.Empty(session.CrashFrames);
        }
    }
}
