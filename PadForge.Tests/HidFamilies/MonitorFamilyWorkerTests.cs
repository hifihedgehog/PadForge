using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using PadForge.Tests.MonitorFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The monitor families on the shared worker (#557): what a
    /// claim sends through the worker's own clock, the LG keep-alive, the
    /// ViewSonic hold, and the map an ASUS session reads.</summary>
    [Collection("PeripheralOutputStatics")]
    public class MonitorFamilyWorkerTests
    {
        private static HidCollectionInfo CollectionOf(string folder, string name = null)
        {
            var registrations = OpenRgbRegistrations.Of(folder).ToList();
            var r = name == null ? registrations[0] : registrations.Single(x => x.Name == name);
            return MonitorTest.CollectionOf(r);
        }

        private static IReadOnlyList<IHidLightingFamily> Only(IHidLightingFamily family) => new[] { family };

        /// <summary>KeepaliveThread (RGBController_LGMonitor.cpp:146-157):
        /// the picture goes out again 500 ms after the last one, with no
        /// second power-on or mode frame.</summary>
        [Fact]
        public void AnLgClaim_IsKeptAliveEvery500Ms()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 65 };
            var collection = CollectionOf(LgMonitorLighting.OpenRgbFamily);
            using var backend = rig.Lit(Only(LgMonitorLighting.Instance), collection, 0x00FF00);
            Assert.Equal(5, rig.Log.Of('O').Count);
            rig.Log.Clear();
            rig.Advance(LgMonitorLighting.KeepAliveMs - 1);
            KeyboardFamilyRig.Passes(backend);
            Assert.Empty(rig.Log.Of('O'));
            rig.Advance(1);
            KeyboardFamilyRig.Passes(backend);
            var again = rig.Log.Of('O');
            Assert.Equal(3, again.Count);
            Assert.Equal("00 53 43 C1 02 91 00 00 FF 00", ReportLog.Hex(again[0], 10));
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
            Assert.Empty(backend.Recorded);
        }

        /// <summary>A ViewSonic picture is stored, so a changed one waits 150
        /// ms and pictures go 500 ms apart.</summary>
        [Fact]
        public void AViewSonicClaim_HoldsAndSpacesItsWrites()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 167 };
            var collection = CollectionOf(ViewSonicMonitorLighting.OpenRgbFamily, "ViewSonic Monitor XG270QC");
            using var backend = rig.Lit(Only(ViewSonicMonitorLighting.Instance), collection, 0x0000FF,
                holdMs: ViewSonicMonitorLighting.HoldMs);
            var first = Assert.Single(rig.Log.Of('F'));
            Assert.Equal("02 01 00 00 FF 00 0A 00 01 00 00 FF 00 0A 00 01", ReportLog.Hex(first, 16));
            Assert.Equal(167, first.Length);
            KeyboardFamilyRig.Light(0xFF0000);
            KeyboardFamilyRig.Passes(backend);
            rig.Advance(ViewSonicMonitorLighting.HoldMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Single(rig.Log.Of('F'));
            rig.Advance(ViewSonicMonitorLighting.RepaintMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(2, rig.Log.Of('F').Count);
            Assert.Equal("02 01 FF 00 00", ReportLog.Hex(rig.Log.Of('F')[1], 5));
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }

        /// <summary>An ASUS session's count becomes the unit's map, and the
        /// picture fills it.</summary>
        [Fact]
        public void AnAsusClaim_PublishesTheCountItRead()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 65, InputLength = 65 };
            rig.Log.InputAnswer = sent =>
            {
                if (sent[1] != 0xB0) return null;
                var answer = new byte[65];
                answer[0] = 0xEC;
                answer[32] = 4;
                return answer;
            };
            var collection = CollectionOf(AsusMonitorLighting.OpenRgbFamily);
            using var backend = rig.Lit(Only(AsusMonitorLighting.Instance), collection, 0x123456);
            var map = PeripheralLightMaps.For(KeyboardFamilyRig.PathOf(collection));
            Assert.Equal(4, map.Lights.Count);
            var sent = rig.Log.Of('O');
            Assert.Equal(3, sent.Count);
            Assert.Equal("EC B0", ReportLog.Hex(sent[0], 2));
            Assert.Equal("EC 35", ReportLog.Hex(sent[1], 2));
            Assert.Equal("EC 40 84 00 04 12 34 56 12 34 56 12 34 56 12 34 56 00", ReportLog.Hex(sent[2], 18));
        }

        /// <summary>An AW3225QF claim: Initialize, then one write to 0B for
        /// one color.</summary>
        [Fact]
        public void AnAlienwareClaim_InitializesThenLights()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 65 };
            var collection = CollectionOf(AlienwareMonitorLighting.OpenRgbFamily, "Alienware AW3225QF");
            using var backend = rig.Lit(Only(AlienwareMonitorLighting.Instance), collection, 0x102030);
            var sent = rig.Log.Of('O');
            Assert.Equal(4, sent.Count);
            Assert.Equal("00 95", ReportLog.Hex(sent[0], 2));
            Assert.Equal("00 92 37 0A 00 51 87 D0 04 0B 10 20 30 64", ReportLog.Hex(sent[3], 14));
            Assert.Equal(4, rig.Waits.Count(w => w == AlienwareMonitorLighting.QfWaitMs));
        }

        /// <summary>On the shared channel, which reads no input report on
        /// request, an AW3423DWF unit is listed and never written to.</summary>
        [Fact]
        public void AnAw3423DwfClaim_OnTheSharedChannel_WritesNothing()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 193, InputLength = 193 };
            var collection = CollectionOf(AlienwareMonitorLighting.OpenRgbFamily, "Alienware AW3423DWF");
            using var backend = rig.Lit(Only(AlienwareMonitorLighting.Instance), collection, 0x102030);
            Assert.Single(backend.Snapshot.Units);
            Assert.Empty(rig.Log.Sent());
        }
    }
}
