using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Recording;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The summary a problem recording (#597) opens with. Each rule gets a
    /// small recording written by hand that sets it off and one that comes
    /// close and does not, and the summary names the first place on an
    /// input's path where something went wrong.
    /// </summary>
    public class RecordingSummaryTests
    {
        private const string Pad = "D1 name=\"Test Pad\" vid=054C pid=0CE6 type=Gamepad rumble=1";

        private static string Xbox => RecordingCodec.TypeWord((int)VirtualControllerType.Xbox);

        private static string ButtonA => "0x" + Gamepad.A.ToString("X4", CultureInfo.InvariantCulture);

        /// <summary>A recording as the recorder lays it out, with the parts
        /// the summary reads.</summary>
        private static string File(IEnumerable<string> devices, IEnumerable<string> settings, IEnumerable<string> events)
        {
            var sb = new StringBuilder();
            void Line(string text) => sb.Append(text).Append("\r\n");
            Line(RecordingCodec.FirstLine);
            Line(RecordingCodec.StatusRecording);
            Line(RecordingCodec.DevicesHeader);
            foreach (string d in devices) Line(d);
            Line(RecordingCodec.ControllersHeader);
            Line("C1 type=" + Xbox + " enabled=1");
            Line(RecordingCodec.SettingsHeader);
            foreach (string s in settings) Line(s);
            Line(RecordingCodec.EventsHeader);
            foreach (string e in events) Line(e);
            Line(RecordingCodec.EndHeader);
            return sb.ToString();
        }

        private static RecordingSummary Summarize(IEnumerable<string> devices, IEnumerable<string> settings, params string[] events)
        {
            using var reader = new RecordingReader(new StringReader(File(devices, settings, events)));
            return RecordingSummary.Read(reader);
        }

        private static RecordingSummary Summarize(params string[] events) => Summarize(new[] { Pad }, Array.Empty<string>(), events);

        private static RecordingFinding Only(RecordingSummary summary, FindingPlace place)
            => Assert.Single(summary.Findings, f => f.Place == place);

        private static void None(RecordingSummary summary, FindingPlace place)
            => Assert.DoesNotContain(summary.Findings, f => f.Place == place);

        // ── The device or its driver ──

        [Fact]
        public void AnAxisThatNeverCameToRest_PointsAtTheDevice()
        {
            var summary = Summarize(
                "0.000000 I D1 full online a0=50000 a1=32768",
                "1.000000 I D1 a0=51000",
                "6.000000 I D1 a0=50500");
            var finding = Only(summary, FindingPlace.Device);
            Assert.Equal("D1 Axis 0 never came to rest: it stayed between 50000 and 51000 for the whole recording. Look at the device or its driver.", finding.Text);
            Assert.Equal("First place to look: the device or its driver.", summary.Lines()[1]);
        }

        [Fact]
        public void AnAxisThatCameBackToCenter_IsNoFinding()
        {
            var summary = Summarize(
                "0.000000 I D1 full online a0=50000 a1=32768",
                "3.000000 I D1 a0=32768",
                "6.000000 I D1 a0=50500");
            Assert.Empty(summary.Findings);
            Assert.Equal("First place to look: nothing stood out in what was recorded.", summary.Lines()[1]);
        }

        [Fact]
        public void AnAxisOnADeviceOnlineUnderFiveSeconds_IsNotJudged()
        {
            var summary = Summarize(
                "0.000000 I D1 full online a0=50000",
                "4.000000 I D1 a0=51000");
            Assert.Empty(summary.Findings);
        }

        [Fact]
        public void AButtonDownForTheWholeRecording_PointsAtTheDevice()
        {
            var summary = Summarize(
                "0.000000 I D1 full online a1=32768 b5+",
                "6.000000 I D1 a1=33000");
            Assert.Equal("D1 Button 5 was down for the whole recording. Look at the device or its driver.", Only(summary, FindingPlace.Device).Text);

            var released = Summarize(
                "0.000000 I D1 full online a1=32768 b5+",
                "2.000000 I D1 b5-",
                "6.000000 I D1 a1=33000");
            None(released, FindingPlace.Device);
        }

        [Fact]
        public void AHatOffCenterForTheWholeRecording_PointsAtTheDevice()
        {
            var summary = Summarize(
                "0.000000 I D1 full online h0=9000",
                "6.000000 I D1 a1=33000");
            Assert.Equal("D1 POV 0 was off center for the whole recording. Look at the device or its driver.", Only(summary, FindingPlace.Device).Text);

            None(Summarize(
                "0.000000 I D1 full online h0=9000",
                "1.000000 I D1 h0=-1",
                "6.000000 I D1 a1=33000"), FindingPlace.Device);
        }

        [Theory]
        [InlineData("read-failed", "read failed")]
        [InlineData("handle-lost", "handle lost")]
        [InlineData("gone", "gone")]
        public void ADeviceThatWentOffline_PointsAtTheDevice_WithItsReason(string reason, string words)
        {
            var summary = Summarize(
                "0.000000 I D1 full online a1=32768",
                "2.000000 I D1 offline=" + reason,
                "3.000000 I D1 full online a1=32768");
            var finding = Only(summary, FindingPlace.Device);
            Assert.Equal("D1 went offline at 2.000 s (" + words + "). Look at the device or its driver.", finding.Text);
            Assert.Equal(2_000_000, finding.Microseconds);
            Assert.Contains(summary.Lines(), l => l.StartsWith("D1 Test Pad: ", StringComparison.Ordinal) && l.Contains("went offline 1 time"));
        }

        [Fact]
        public void ADeviceThatStayedOnline_IsNoFinding()
            => Assert.Empty(Summarize(
                "0.000000 I D1 full online a1=32768",
                "2.000000 I D1 a1=40000",
                "3.000000 I D1 a1=32768").Findings);

        // ── The settings ──

        private static readonly string[] Row =
        {
            "assign device=D1 controller=C1",
            "row controller=C1 target=ButtonA layer=Base sources=1 src1=D1 in1=\"Button 0\" kind1=Direct dz1=0",
        };

        [Fact]
        public void AnInputThatMovedWhileItsRowsTargetNeverDid_PointsAtTheSettings_AndNamesTheRow()
        {
            var summary = Summarize(new[] { Pad }, Row,
                "0.000000 I D1 full online a1=32768",
                "0.000000 S C1 sent=ok type=" + Xbox,
                "0.000000 O C1 full type=" + Xbox,
                "1.000000 I D1 b0+",
                "1.500000 I D1 b0-",
                "2.000000 O C1 lx=100",
                "7.000000 I D1 a1=33000");
            var finding = Only(summary, FindingPlace.Settings);
            Assert.Equal("D1 Button 0 was pressed 1 time and C1 ButtonA never changed. Look at the settings: the mapping row ButtonA of C1.", finding.Text);
            Assert.Equal("First place to look: the settings.", summary.Lines()[1]);
            Assert.Contains(summary.Lines(), l => l.Contains("1 mapping row checked and 0 not."));
        }

        [Fact]
        public void AnInputWhoseRowsTargetFollowed_IsNoFinding()
        {
            var summary = Summarize(new[] { Pad }, Row,
                "0.000000 I D1 full online a1=32768",
                "0.000000 S C1 sent=ok type=" + Xbox,
                "0.000000 O C1 full type=" + Xbox,
                "1.000000 I D1 b0+",
                "1.000000 O C1 bt=" + ButtonA,
                "1.500000 I D1 b0-",
                "1.500000 O C1 bt=0x0000",
                "7.000000 I D1 a1=33000");
            Assert.Empty(summary.Findings);
        }

        [Fact]
        public void ARowWhoseInputNeverMoved_IsNotJudged()
        {
            var summary = Summarize(new[] { Pad }, Row,
                "0.000000 I D1 full online a1=32768",
                "0.000000 S C1 sent=ok type=" + Xbox,
                "0.000000 O C1 full type=" + Xbox,
                "7.000000 I D1 a1=33000");
            Assert.Empty(summary.Findings);
            Assert.Contains(summary.Lines(), l => l.Contains("0 mapping rows checked and 1 not."));
        }

        // ── The virtual controller ──

        [Theory]
        [InlineData("absent", "it had no virtual controller")]
        [InlineData("disconnected", "its virtual controller was not connected")]
        [InlineData("failed", "the send failed")]
        public void OutputThatChangedWhileItWasNotSent_PointsAtTheVirtualController(string sent, string why)
        {
            var summary = Summarize(
                "0.000000 I D1 full online a1=32768",
                "0.000000 S C1 sent=" + sent + " type=" + Xbox,
                "0.000000 O C1 full type=" + Xbox,
                "1.000000 O C1 lx=5000",
                "1.010000 O C1 lx=6000");
            var finding = Only(summary, FindingPlace.VirtualController);
            Assert.Equal("C1 changed 2 times while " + why + ", first at 1.000 s. Look at the virtual controller.", finding.Text);
        }

        [Fact]
        public void OutputThatChangedWhileItWasSent_IsNoFinding()
        {
            var summary = Summarize(
                "0.000000 I D1 full online a1=32768",
                "0.000000 S C1 sent=ok type=" + Xbox,
                "0.000000 O C1 full type=" + Xbox,
                "1.000000 O C1 lx=5000");
            Assert.Empty(summary.Findings);
            Assert.Contains(summary.Lines(), l => l.StartsWith("C1: 1 change, ok 1.000 s", StringComparison.Ordinal));
        }

        // ── Feedback ──

        [Fact]
        public void AGamesRumbleWithNoWriteToTheDevice_PointsAtFeedback()
        {
            var summary = Summarize(new[] { Pad }, new[] { "assign device=D1 controller=C1" },
                "0.000000 I D1 full online a1=32768",
                "1.000000 F C1 rumble=30000,30000,0,0",
                "3.000000 I D1 a1=40000");
            var finding = Only(summary, FindingPlace.Feedback);
            Assert.Equal("A game asked C1 to rumble 1 time and none of its devices was written within a second, first at 1.000 s. Look at feedback.", finding.Text);
        }

        [Fact]
        public void AGamesRumbleTheDeviceWasWrittenAfter_IsNoFinding()
        {
            var summary = Summarize(new[] { Pad }, new[] { "assign device=D1 controller=C1" },
                "0.000000 I D1 full online a1=32768",
                "1.000000 F C1 rumble=30000,30000,0,0",
                "1.200000 F D1 wrote=30000,30000,0,0",
                "3.000000 I D1 a1=40000");
            Assert.Empty(summary.Findings);
        }

        [Fact]
        public void AGamesRumbleToAControllerWithNoMotorBehindIt_IsNoFinding()
        {
            string noMotor = Pad.Replace("rumble=1", "rumble=0");
            var summary = Summarize(new[] { noMotor }, new[] { "assign device=D1 controller=C1" },
                "0.000000 I D1 full online a1=32768",
                "1.000000 F C1 rumble=30000,30000,0,0",
                "3.000000 I D1 a1=40000");
            Assert.Empty(summary.Findings);
        }

        // ── Timing ──

        [Fact]
        public void AStallNearAMarker_PointsAtTiming()
        {
            var summary = Summarize(
                "0.000000 I D1 full online a1=32768",
                "1.000000 M - mark stick drifted",
                "3.000000 T - stall sdl=31 sweep=0 pass=52",
                "4.000000 I D1 a1=33000");
            var finding = Only(summary, FindingPlace.Timing);
            Assert.StartsWith("The engine stalled at 3.000 s, +2.000 s from the marker at 1.000 s", finding.Text);
            var lines = summary.Lines();
            int marker = lines.IndexOf("Marker at 1.000 s: stick drifted");
            Assert.True(marker > 0);
            Assert.StartsWith("  Before, 5 s: ", lines[marker + 1]);
            Assert.StartsWith("  After, 5 s: ", lines[marker + 2]);
            Assert.Contains(lines.Skip(marker + 3), l => l.Contains("+2.000 s stall sdl=31"));
        }

        [Fact]
        public void AStallFarFromAnyMarker_IsNoFinding()
            => None(Summarize(
                "0.000000 I D1 full online a1=32768",
                "1.000000 M - mark",
                "20.000000 T - stall sdl=31 sweep=0 pass=52",
                "21.000000 I D1 a1=33000"), FindingPlace.Timing);

        [Fact]
        public void ASlowSecondNearAMarker_PointsAtTiming_AndAFullSecondDoesNot()
        {
            const string slow = " T - passes=500 set=1000 rate=500.000 work=100.0 longest=900 capture=1.5 late=0.0 latest=0 sleeps=500";
            var summary = Summarize(
                "0.000000 I D1 full online a1=32768",
                "2.000000 M - mark",
                "3.000000" + slow,
                "4.000000 I D1 a1=33000");
            Assert.StartsWith("The engine ran slow at 3.000 s", Only(summary, FindingPlace.Timing).Text);
            Assert.Contains(summary.Lines(), l => l.StartsWith("Timing: 1000 passes a second set, slowest second 500", StringComparison.Ordinal));

            None(Summarize(
                "0.000000 I D1 full online a1=32768",
                "2.000000 M - mark",
                "3.000000 T - passes=1000 set=1000 rate=1000.000 work=100.0 longest=900 capture=1.5 late=0.0 latest=0 sleeps=1000",
                "4.000000 I D1 a1=33000"), FindingPlace.Timing);
        }

        // ── The order, and what the summary says of every device ──

        [Fact]
        public void TheFirstPlaceToLook_IsTheEarliestOnAnInputsPath()
        {
            var summary = Summarize(new[] { Pad }, Row,
                "0.000000 I D1 full online a1=32768",
                "0.000000 S C1 sent=ok type=" + Xbox,
                "0.000000 O C1 full type=" + Xbox,
                "1.000000 I D1 b0+",
                "1.500000 I D1 b0-",
                "2.000000 I D1 offline=read-failed",
                "2.500000 I D1 full online a1=32768",
                "7.000000 I D1 a1=33000");
            Assert.Equal(new[] { FindingPlace.Device, FindingPlace.Settings }, summary.Findings.Select(f => f.Place));
            Assert.Equal("First place to look: the device or its driver.", summary.Lines()[1]);
        }

        [Fact]
        public void EachDevice_HasItsReportRate_ItsLongestGap_AndItsRestPerAxis()
        {
            var events = new List<string> { "0.000000 I D1 full online a0=32768 a1=32768" };
            // A stick moved for half a second at 1,000 reports a second, one
            // report 8 ms late, then left at rest.
            for (int i = 1; i <= 500; i++)
            {
                long micros = 1_000_000 + i * 1_000 + (i > 250 ? 7_000 : 0);
                var sb = new StringBuilder();
                RecordingCodec.AppendTime(sb, micros);
                events.Add(sb + " I D1 a0=" + (32768 + i * 10).ToString(CultureInfo.InvariantCulture));
            }
            events.Add("2.000000 I D1 a0=32768");
            events.Add("9.000000 I D1 a1=32800");
            var lines = Summarize(events.ToArray()).Lines();
            string device = Assert.Single(lines, l => l.StartsWith("D1 Test Pad: ", StringComparison.Ordinal));
            Assert.Contains("about 1000 a second while moving", device);
            Assert.Contains("longest gap while moving 8.0 ms", device);
            string rest = lines[lines.IndexOf(device) + 1];
            Assert.StartsWith("  Rest: a0 32768 (32768 to 37768), a1 32768", rest);
        }

        [Fact]
        public void LinesFromBeforeTheStart_AreContext_AndNoRuleJudgesThem()
        {
            var summary = Summarize(
                "-30.000000 L - STALL poll sdl=31ms enum=0ms cycle=52ms",
                "-2.000000 I D1 offline=read-failed",
                "0.000000 I D1 full online a1=32768",
                "6.000000 I D1 a1=33000");
            Assert.Empty(summary.Findings);
            Assert.Contains(summary.Lines(), l => l.Contains("1 diagnostics line"));
        }

        [Fact]
        public void ChangesTheRecorderHadNoRoomFor_AreCountedInTheSummary()
        {
            var summary = Summarize(
                "0.000000 I D1 full online a1=32768",
                "0.100000 T - lost=42",
                "6.000000 I D1 a1=33000");
            Assert.Contains(summary.Lines(), l => l.EndsWith("42 changes did not fit and were left out.", StringComparison.Ordinal));
        }
    }
}
