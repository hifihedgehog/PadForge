using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using PadForge.Tests.LaptopFamilies;
using Xunit;
using static PadForge.Tests.AsusAuraUsbLightingTests;

namespace PadForge.Tests
{
    /// <summary>The pipe each frame takes, what a family accepts and what it
    /// hands back, against the references the families follow (#557).</summary>
    public class FamilyWireAndHandBackTests
    {
        private static string HexOf(byte[] bytes, int count) => LaptopFamilyTest.Hex(bytes, count);

        private static OpenRgbRegistration Reg(string family, int product)
            => OpenRgbRegistrations.Of(family).First(r => r.ProductId == product);

        // ── AlienFX ──

        /// <summary>AddV8DataBlock: the light, 81, the tempo, A5, the time, 0A, the color,
        /// the second color and 02, thirteen bytes at 5, 20, 35 and 50
        /// (AlienFX_SDK.cpp:364-369 and 385).</summary>
        [Fact]
        public void TheV8Blocks_CarryTheBrightnessByte_AheadOfTheirColors()
        {
            var frame = AlienFxLighting.V8Picture(0xFF0000)[0];
            Assert.Equal("00 81 00 A5 00 0A FF 00 00 FF 00 00 02", HexOf(frame.Skip(5).ToArray(), 13));
            Assert.Equal("03 81 00 A5 00 0A FF 00 00 FF 00 00 02", HexOf(frame.Skip(50).ToArray(), 13));
        }

        /// <summary>A report with no room for one key's four bytes has no protocol, so
        /// a picture never builds on it. Eight bytes hold one key.</summary>
        [Fact]
        public void TheV5Protocol_NeedsRoomForOneKey()
        {
            Assert.Null(AlienFxLighting.ApiOf(new HidCollectionInfo { Path = "fake", VendorId = 0x0D62, ProductId = 0xCA01, Usage = 0xCC, FeatureReportLength = 7 }));
            Assert.Equal(AlienFxLighting.Api.V5, AlienFxLighting.ApiOf(new HidCollectionInfo { Path = "fake", VendorId = 0x0D62, ProductId = 0xCA01, Usage = 0xCC, FeatureReportLength = 8 }));
            Assert.Equal(AlienFxLighting.V5Lights, AlienFxLighting.V5Picture(8, 0x112233).Count);
        }

        /// <summary>PrepareAndSend (AlienFX_SDK.cpp:113-126): HidD_SetOutputReport on V2 to V4,
        /// WriteFile on V6 and on.</summary>
        [Theory]
        [InlineData(AlienFxLighting.Api.V2, "setout")]
        [InlineData(AlienFxLighting.Api.V3, "setout")]
        [InlineData(AlienFxLighting.Api.V4, "setout")]
        [InlineData(AlienFxLighting.Api.V6, "out")]
        [InlineData(AlienFxLighting.Api.V7, "out")]
        [InlineData(AlienFxLighting.Api.V8, "out")]
        internal void TheOutputReports_TakeThePipeTheSdkSendsThemOn(AlienFxLighting.Api api, string kind)
        {
            var io = new FakeReportChannel { InputReportLength = 65 };
            Assert.Equal(HidLightingPaint.Sent, new AlienFxLighting.Session(api).Paint(io, _ => { }, null, new[] { 0x112233 }));
            var pipes = io.Calls.Select(c => c.Kind).Where(k => k is "out" or "setout").Distinct().ToList();
            Assert.Equal(new[] { kind }, pipes);
        }

        // ── ASUS laptops ──

        private static FakeReportChannel AsusLaptop(int kind)
        {
            var answer = new byte[64];
            answer[0] = 0x5D;
            answer[1] = 0x05;
            answer[2] = 0x20;
            answer[3] = 0x31;
            answer[9] = (byte)kind;
            answer[12] = 1;
            var io = new FakeReportChannel { FeatureReportLength = 64, OutputReportLength = 64 };
            io.Feature = id => id == 0x5D ? answer : null;
            return io;
        }

        /// <summary>G-Helper writes C0 03 01 and the rear light's three frames with
        /// stream.Write (Aura.cs:388-389 and 863, AsusHid.cs:160-173).</summary>
        [Fact]
        public void TheZ13sOpening_AndItsRearLight_GoAsOutputReports()
        {
            var io = AsusLaptop(4);
            AsusLaptopAura.Begin(io, _ => { }, 0x1A30, new AsusLaptopMachine("ROG Flow Z13", "GZ302EA"));
            Assert.Contains(io.Calls, c => c.Kind == "out" && HexOf(c.Bytes, 4) == "5D C0 03 01");
            Assert.DoesNotContain(io.Calls, c => c.Kind == "set" && HexOf(c.Bytes, 2) == "5D C0");

            var rear = new FakeReportChannel { FeatureReportLength = 64, OutputReportLength = 64 };
            var session = AsusLaptopAura.Begin(rear, _ => { }, 0x18C6, new AsusLaptopMachine("ROG Flow Z13", "GZ301ZE"));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(rear, _ => { }, session.Map.Lights, new[] { 0x112233 }));
            Assert.Equal(new[] { "5D B3", "5D B5", "5D B4" }, rear.Calls.Where(c => c.Kind == "out").Select(c => HexOf(c.Bytes, 2)));
            Assert.DoesNotContain(rear.Calls, c => c.Kind == "set");
        }

        /// <summary>A feature report that fails goes the other way at every step of the
        /// family, as the query already does, and each way is the other's fallback.</summary>
        [Fact]
        public void AnAsusLaptopFrame_TheFirstWayRefuses_GoesTheOtherWay()
        {
            var machine = new AsusLaptopMachine("ROG Zephyrus G14", "GA402RJ");
            var io = AsusLaptop(4);
            io.FailFeatureWrites = true;
            var session = AsusLaptopAura.Open(io, _ => { }, 0x19B6, machine, new AsusLaptopAura.Answer(4, 1, 0, 0));
            Assert.NotNull(session);
            Assert.Contains(io.Calls, c => c.Kind == "out" && HexOf(c.Bytes, 2) == "5D BC");
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, session.Map.Lights, new[] { 0x112233 }));

            // No output report: the Z13's opening goes as a feature report.
            var featureOnly = new FakeReportChannel { FeatureReportLength = 64 };
            AsusLaptopAura.Open(featureOnly, _ => { }, 0x1A30, new AsusLaptopMachine("ROG Flow Z13", "GZ302EA"), new AsusLaptopAura.Answer(4, 1, 0, 0));
            Assert.Contains(featureOnly.Calls, c => c.Kind == "set" && HexOf(c.Bytes, 4) == "5D C0 03 01");
            Assert.DoesNotContain(featureOnly.Calls, c => c.Kind == "out");
        }

        // ── Legion K500 ──

        /// <summary>OpenRGB sends the feature report and writes the mode report whatever
        /// the first answered (LenovoK500Controller.cpp:167-173).</summary>
        [Fact]
        public void TheK500_SendsItsMode_WhenThePreludeIsRefused()
        {
            var io = new FakeReportChannel { OutputReportLength = 65, FailFeatureWrites = true };
            var lights = LenovoK500Lighting.Lights();
            var session = new LenovoK500Lighting.Session();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, new int[lights.Count]));
            Assert.Equal("00 01", HexOf(io.Calls.Single(c => c.Kind == "out").Bytes, 2));
            io.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, lights, new int[lights.Count]));
        }

        // ── Dell G series ──

        /// <summary>UpdateDim bails on a refusal and leaves dirty_dim set, and nothing looks
        /// at that: the colors go out and the next update tries the dim level again
        /// (AlienwareController.cpp:714-745 and 1020-1024).</summary>
        [Fact]
        public void ARefusedDim_LeavesTheColorsToGoOut_AndIsTriedAgain()
        {
            var io = new LoggingHidChannel { FeatureReportLength = 34 };
            bool refuse = true;
            io.Echo = sent =>
            {
                var answer = (byte[])sent.Clone();
                if (sent[2] == AlienwareDimCommand && refuse) return answer;
                answer[20] ^= 0x5A;
                answer[5] = 0;
                return answer;
            };
            var session = new AlienwareLighting.Session(AlienwareLighting.MapOf(0x0C01, 4, 0x0550));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, session.Map.Lights, new int[4]));
            Assert.Contains(io.Sent("F"), f => f[2] == 0x24);

            io.Log.Clear();
            refuse = false;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, session.Map.Lights, new int[4]));
            Assert.Contains(io.Sent("F"), f => f[2] == AlienwareDimCommand);

            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, session.Map.Lights, new int[4]));
            Assert.DoesNotContain(io.Sent("F"), f => f[2] == AlienwareDimCommand);

            // A dim report that never reached the controller still fails the picture.
            var dead = new LoggingHidChannel { FeatureReportLength = 34, FailWrites = true };
            var fresh = new AlienwareLighting.Session(AlienwareLighting.MapOf(0x0C01, 4, 0x0550));
            Assert.Equal(HidLightingPaint.Failed, fresh.Paint(dead, dead.Wait, fresh.Map.Lights, new int[4]));
        }

        private const byte AlienwareDimCommand = 0x26;

        // ── Aura mice and mousemats ──

        /// <summary>A device that answers 12 03 by <paramref name="answer"/>, given the frame
        /// that asked, and is silent on any other frame and on a second read.</summary>
        private static FakeReportChannel Answers(Func<byte[], byte[]> answer)
        {
            var io = new FakeReportChannel { InputReportLength = 65, OutputReportLength = 65, FeatureReportLength = 0 };
            int seen = 0;
            io.Input = () =>
            {
                var outs = io.Calls.Where(c => c.Kind == "out").ToList();
                bool fresh = outs.Count > seen;
                seen = outs.Count;
                if (!fresh) return null;
                var last = outs[^1].Bytes;
                return last[1] == 0x12 && last[2] == 0x03 ? answer(last) : null;
            };
            return io;
        }

        private static byte[] Report(byte id, params (int At, byte Value)[] bytes)
        {
            var report = new byte[65];
            report[0] = id;
            report[1] = 0x12;
            report[2] = 0x03;
            foreach (var (at, value) in bytes) report[at] = value;
            return report;
        }

        private static List<string> Outs(FakeReportChannel io, int count = 13)
            => io.Calls.Where(c => c.Kind == "out").Select(c => HexOf(c.Bytes, count)).ToList();

        private static HidLightingModel AuraModel(int product)
            => new AsusAuraUsbLighting().ModelFor(Reg(AsusAuraUsbLighting.OpenRgbFamily, product), product);

        /// <summary>A session that has shown a picture, with its channel's log empty.</summary>
        private static HidLightingSession Lit(HidLightingSession session, int product, FakeReportChannel io)
        {
            var lights = AuraModel(product).Map.Lights;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, new int[lights.Count]));
            io.Calls.Clear();
            return session;
        }

        /// <summary>The crash frames of a session begun on <paramref name="io"/>, once it shows a picture.</summary>
        private static byte[][] CrashOf(int product, FakeReportChannel io)
            => Lit(AuraModel(product).Begin(io, _ => { }), product, io).CrashFrames;

        /// <summary>GetReadLightingModePacket and ParseLightingSetting (AsusMouse.cs:2055-2125):
        /// each zone by its place, the mode at 5, the brightness at 6, the color at 7, the
        /// direction at 11, random at 12 and the speed at 13. The hand-back is the 51 28 frame
        /// with those values and no 50 03.</summary>
        [Fact]
        public void AMouseThatAnswersPerZone_GetsEachZoneBack_WithNoSave()
        {
            var io = Answers(ask => ask[3] switch
            {
                0 => Report(0, (5, 0), (6, 0x40), (7, 0x11), (8, 0x22), (9, 0x33), (11, 7), (12, 1), (13, 9)),
                1 => Report(0, (5, 3), (6, 0x50), (11, 1), (12, 1), (13, 5)),
                _ => Report(0, (5, 5), (6, 0x60), (7, 0x44), (8, 0x55), (9, 0x66), (11, 1), (12, 1), (13, 9)),
            });
            var model = AuraModel(0x1A18);
            var session = model.Begin(io, _ => { });
            Assert.Equal(new[] { "00 12 03 00", "00 12 03 01", "00 12 03 02" }, Outs(io, 4));
            Assert.All(io.Calls.Where(c => c.Kind == "out"), c => Assert.Equal(65, c.Bytes.Length));
            Assert.Empty(session.CrashFrames);

            Lit(session, 0x1A18, io);
            var crash = session.CrashFrames.Select(f => HexOf(f, 13)).ToList();
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(new[]
            {
                // Static takes no direction, random or speed.
                "00 51 28 00 00 00 40 11 22 33 00 00 00",
                // Wave takes a direction and a speed, Comet a direction and random
                // (RGBController_AsusAuraMouse.cpp:121 and 153, AsusMouse.cs:1970-1983).
                "00 51 28 01 00 03 50 00 00 00 01 00 05",
                "00 51 28 02 00 05 60 44 55 66 01 01 00",
            }, Outs(io));
            Assert.DoesNotContain(io.Calls, c => c.Kind == "out" && c.Bytes[1] == 0x50 && c.Bytes[2] == 0x03);
            Assert.Equal(Outs(io), crash);
            Assert.Empty(session.CrashFrames);
        }

        /// <summary>The older mice answer every zone in one report, five bytes a zone from 5 by
        /// the zone's number, with one direction, random and speed at 21 to 23 (GladiusII.cs:157-211,
        /// Chakram.cs:115-146, Pugio.cs:152-203, StrixImpact.cs:119-170).</summary>
        [Fact]
        public void AMouseThatAnswersInOneReport_IsAskedOnce_AndReadByZone()
        {
            var io = Answers(_ => Report(0,
                (5, 0), (6, 4), (7, 0xFF), (8, 0x00), (9, 0x80),
                (10, 0), (11, 3), (12, 0x00), (13, 0xFF), (14, 0xFF),
                (15, 3), (16, 2), (21, 1), (23, 7)));
            var session = AuraModel(0x1845).Begin(io, _ => { });
            Assert.Equal(new[] { "00 12 03 00" }, Outs(io, 4));

            Lit(session, 0x1845, io);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(new[]
            {
                "00 51 28 00 00 00 04 FF 00 80 00 00 00",
                "00 51 28 01 00 00 03 00 FF FF 00 00 00",
                "00 51 28 02 00 03 02 00 00 00 01 00 07",
            }, Outs(io));
        }

        /// <summary>The PNK LTD's read has offsets that disagree with its own dump in G-Helper
        /// (GladiusII.cs:365-397), and G-Helper does not list these others, so nothing says
        /// how they answer and nothing is asked.</summary>
        [Theory]
        [InlineData(0x18CD)]
        [InlineData(0x189E)]
        [InlineData(0x1C8D)]
        [InlineData(0x1981)]
        [InlineData(0x1962)]
        public void AMouseWithNoKnownRead_IsNotAsked_AndHandsBackNothing(int product)
        {
            var io = Answers(_ => Report(0, (5, 1), (6, 1)));
            var session = AuraModel(product).Begin(io, _ => { });
            Assert.Empty(io.Calls);
            Lit(session, product, io);
            Assert.Empty(session.CrashFrames);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Empty(io.Calls);
        }

        /// <summary>A mouse that does not answer costs one read and hands back nothing. An answer
        /// naming a mode the mouse's list leaves empty, FF AA, or another report's answer leaves
        /// that zone alone.</summary>
        [Fact]
        public void AMouseThatGivesNoUsableAnswer_HandsBackNothing()
        {
            var silent = new FakeReportChannel { InputReportLength = 65, OutputReportLength = 65, FeatureReportLength = 0 };
            var session = AuraModel(0x1A18).Begin(silent, _ => { });
            Assert.Single(silent.Calls, c => c.Kind == "out");
            Assert.Empty(Lit(session, 0x1A18, silent).CrashFrames);

            // The list of 0x1960 leaves a place empty (AsusAuraMouseDevices.h).
            byte hole = (byte)Array.IndexOf(AsusAuraTables.Mouse(0x1960).Modes, (byte)255);
            Assert.NotEqual(255, hole);
            Assert.Empty(CrashOf(0x1960, Answers(_ => Report(0, (5, hole), (6, 1)))));
            Assert.Empty(CrashOf(0x1A18, Answers(_ => Report(0, (5, 200), (6, 1)))));

            var error = Report(0);
            error[1] = 0xFF;
            error[2] = 0xAA;
            Assert.Empty(CrashOf(0x1A18, ErrorAnswers(error)));

            // A brightness past the mouse's maximum goes back at the maximum.
            Assert.Equal(4, CrashOf(0x1845, Answers(_ => Report(0, (6, 0xFF), (11, 4), (16, 4))))[0][6]);
        }

        private static FakeReportChannel ErrorAnswers(byte[] answer)
        {
            var io = new FakeReportChannel { InputReportLength = 65, OutputReportLength = 65, FeatureReportLength = 0 };
            int seen = 0;
            io.Input = () =>
            {
                int outs = io.Calls.Count(c => c.Kind == "out");
                bool fresh = outs > seen;
                seen = outs;
                return fresh ? answer : null;
            };
            return io;
        }

        /// <summary>BalteusQi (Balteus.cs:12 and 100-136): report EE, the mode at 5, the
        /// brightness at 7, the pattern at 8 and the color at 10, and 51 28 with the mode,
        /// the speed by mode, the brightness, the pattern by mode, 00 and the color.</summary>
        [Theory]
        [InlineData(0, 2, 0, "EE 51 28 00 00 00 00 02 FF 00 11 22 33")]
        [InlineData(1, 3, 2, "EE 51 28 00 00 01 01 03 02 00 11 22 33")]
        [InlineData(1, 3, 0, "EE 51 28 00 00 01 01 03 00 00 11 22 33")]
        [InlineData(5, 1, 0, "EE 51 28 00 00 05 02 01 01 00 11 22 33")]
        [InlineData(2, 4, 0, "EE 51 28 00 00 02 01 04 FF 00 11 22 33")]
        public void TheBalteus_GetsItsOwnEffectBack(int mode, int brightness, int pattern, string expected)
        {
            var io = Answers(_ => Report(0xEE, (5, (byte)mode), (7, (byte)brightness), (8, (byte)pattern), (10, 0x11), (11, 0x22), (12, 0x33)));
            var session = AuraModel(0x1891).Begin(io, _ => { });
            Assert.Equal(new[] { "EE 12 03 00" }, Outs(io, 4));
            Lit(session, 0x1891, io);
            Assert.Equal(new[] { expected }, session.CrashFrames.Select(f => HexOf(f, 13)));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(new[] { expected }, Outs(io));
        }

        /// <summary>Rainbow takes its seven colors from byte 10 (Balteus.cs:6 and 134), and a
        /// mode G-Helper does not list goes back as nothing.</summary>
        [Fact]
        public void TheBalteus_Rainbow_CarriesItsColors_AndAnUnknownModeIsLeftAlone()
        {
            var rainbow = CrashOf(0x1890, Answers(_ => Report(0xEE, (5, 3), (7, 4))))[0];
            Assert.Equal("EE 51 28 00 00 03 0E 04 71 00 F5 00 FF", HexOf(rainbow, 13));
            Assert.Equal(0x0D, rainbow[30]);
            Assert.Empty(CrashOf(0x1891, Answers(_ => Report(0xEE, (5, 4)))));
            Assert.Empty(CrashOf(0x1891, new FakeReportChannel { InputReportLength = 65, OutputReportLength = 65 }));
        }

        // ── Roccat and Turtle Beach ──

        private static HidCollectionInfo RoccatRow(OpenRgbRegistration r, int vendor, int product) => new()
        {
            Path = $@"\\?\hid#vid_{vendor:x4}&pid_{product:x4}&mi_{Math.Max(0, r.Interface):x2}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
            VendorId = (ushort)vendor,
            ProductId = (ushort)product,
            InterfaceNumber = r.Interface,
            UsagePage = (ushort)(r.UsagePage < 0 ? 0xFF00 : r.UsagePage),
            Usage = (ushort)(r.Usage < 0 ? 1 : r.Usage),
        };

        /// <summary>A keyboard or pad on Turtle Beach's vendor ID answers to Swarm II, as the
        /// Mini Air rows already do (PluginDevicesBeta.cs:119-120), whichever family's row it
        /// borrows, and the Roccat products beside them keep ROCCAT Swarm.</summary>
        [Fact]
        public void TurtleBeachsDevices_AnswerToSwarmII_AndRoccatsToSwarm()
        {
            var family = new RoccatVulcanLighting();
            foreach (var r in OpenRgbRegistrations.Of(RoccatVulcanLighting.FamilyName).Where(r => r.Detector == RoccatVulcanLighting.Detector))
            {
                var model = family.Match(RoccatRow(r, r.VendorId, r.ProductId));
                Assert.Same(r.VendorId == RoccatVulcanLighting.TurtleBeachVendorId ? VendorPrograms.TurtleBeach : VendorPrograms.Roccat, model.Programs);
            }
            var max = OpenRgbRegistrations.Of(RoccatVulcanLighting.FamilyName).Single(r => r.ProductId == RoccatVulcanLighting.VulcanIIMax);
            Assert.Same(VendorPrograms.Roccat, family.Match(RoccatRow(max, max.VendorId, max.ProductId)).Programs);
            var turtleMax = family.Match(RoccatRow(max, RoccatVulcanLighting.TurtleBeachVendorId, RoccatVulcanLighting.TurtleBeachVulcanIIMax));
            Assert.Same(VendorPrograms.TurtleBeach, turtleMax.Programs);
            Assert.Equal(family.Match(RoccatRow(max, max.VendorId, max.ProductId)).Map.Lights.Count, turtleMax.Map.Lights.Count);

            var pads = new RoccatLighting();
            var mid = OpenRgbRegistrations.Of(RoccatLighting.FamilyName).Single(r => r.Name == "Roccat Sense Aimo Mid");
            Assert.Same(VendorPrograms.Roccat, pads.Match(RoccatRow(mid, mid.VendorId, mid.ProductId)).Programs);
            Assert.Same(VendorPrograms.TurtleBeach, pads.Match(RoccatRow(mid, RoccatVulcanLighting.TurtleBeachVendorId, RoccatLighting.TurtleBeachAimoWide)).Programs);
            Assert.Same(VendorPrograms.Roccat, pads.Match(RoccatRow(mid, mid.VendorId, mid.ProductId)).Programs);
        }

        /// <summary>OpenRGB drains and awaits with no look at the result
        /// (RoccatVulcanKeyboardController.cpp:651-665), and SignalRGB only writes to this
        /// collection, so a LED collection that cannot be read still shows the picture.</summary>
        [Fact]
        public void AVulcanLedCollection_ThatCannotBeRead_StillShowsThePicture()
        {
            var ready = new byte[4];
            ready[0] = RoccatVulcanLighting.ReadyReport;
            ready[1] = 1;
            var control = new FakeReportChannel { FeatureReportLength = 4 };
            control.Feature = _ => ready;
            var led = new FakeReportChannel { OutputReportLength = 65, FailReads = true };
            var map = RoccatVulcanLighting.MapOf(RoccatVulcanLayouts.TableOf(RoccatVulcanLighting.RoccatVendorId, RoccatVulcanLighting.VulcanTkl), RoccatVulcanLayouts.LayoutUs, "fake");
            var session = new RoccatVulcanLighting.Session(control, led, _ => { }, RoccatVulcanLighting.VulcanTkl, 100, map);
            var colors = new int[map.Lights.Count];
            Assert.Equal(HidLightingPaint.Sent, session.Paint(led, _ => { }, map.Lights, colors));
            int modes = control.Calls.Count(c => c.Kind == "set" && c.Bytes[0] == 0x0D);
            Assert.Equal(1, modes);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(led, _ => { }, map.Lights, colors));
            Assert.Equal(1, control.Calls.Count(c => c.Kind == "set" && c.Bytes[0] == 0x0D));
            led.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(led, _ => { }, map.Lights, colors));
        }

        // ── Corsair ──

        /// <summary>The tables OpenRGB holds for these two mice, whose names its else chain loses
        /// (RGBController_CorsairPeripheral.cpp:854-930 against 1283-1304).</summary>
        [Fact]
        public void TheM65Elite_AndTheHarpoonPro_KeepTheirNamedLights()
        {
            var ironclaw = CorsairPeripheralLighting.Lights(CorsairDeviceType.Mouse, CorsairPeripheralLighting.Logical.Normal, "Corsair Ironclaw RGB");
            var m65 = CorsairPeripheralLighting.Lights(CorsairDeviceType.Mouse, CorsairPeripheralLighting.Logical.Normal, "Corsair M65 RGB Elite");
            Assert.Equal(ironclaw, m65);
            Assert.Equal(LightKind.Logo, m65[2].Kind);
            Assert.Equal(LightKind.Status, m65[3].Kind);

            var harpoon = CorsairPeripheralLighting.Lights(CorsairDeviceType.Mouse, CorsairPeripheralLighting.Logical.Normal, "Corsair Harpoon RGB PRO");
            Assert.Equal(LightKind.Logo, harpoon[3].Kind);
            Assert.Equal(LightKind.Zone, harpoon[2].Kind);
            Assert.Equal(Enumerable.Range(0, CorsairPeripheralLighting.MouseZones), harpoon.Select(l => l.Address));

            var plain = CorsairPeripheralLighting.Lights(CorsairDeviceType.Mouse, CorsairPeripheralLighting.Logical.Normal, "Corsair Harpoon RGB");
            Assert.Equal(CorsairPeripheralLighting.MouseZones, plain.Count);
        }

        /// <summary>The K65 Mini's registration names no usage page, so its frame's length
        /// tells the collection: 1025 bytes from OpenRGB and SignalRGB alike
        /// (CorsairK65MiniController.h:16, Corsair_K65_Mini_Keyboard.js:66).</summary>
        [Theory]
        [InlineData(0x1BAF)]
        [InlineData(0x1BCF)]
        public void TheK65Mini_IsTheCollectionWithAFrameLongOutputReport(int product)
        {
            var family = new CorsairPeripheralLighting();
            var collection = CorsairPeripheralLightingTests.Collection((ushort)product, 0xFF00);
            Assert.Null(family.Match(collection with { OutputReportLength = 65 }));
            Assert.Null(family.Match(collection with { OutputReportLength = 0 }));
            Assert.NotNull(family.Match(collection with { OutputReportLength = CorsairPeripheralLighting.K65MiniPacketLength }));
        }

        // ── Cooler Master ──

        /// <summary>A read of Direct is what an earlier run left, and Direct is no lighting of the
        /// device's own: the header gets its first effect, Spectrum, at OpenRGB's defaults
        /// (RGBController_CMARGBController.cpp:236-244), as the MK850 gets effect 1.</summary>
        [Fact]
        public void AnArgbHeaderFoundInDirect_IsHandedBackItsSpectrum()
        {
            var own = new CoolerMasterLighting.ArgbPortState(0x04, 0x01, 0x80, false, 0x010203);
            var direct = new CoolerMasterLighting.ArgbPortState(CoolerMasterLighting.ArgbDirect, 0x03, 0x00, true, 0x070809);
            var session = new CoolerMasterLighting.ArgbSession(new[] { direct, own, own, own, direct }, "fake");
            var frames = session.HandBackFrames;
            Assert.Equal(10, frames.Length);
            Assert.Equal("00 80 01 01", HexOf(frames[0], 4));
            Assert.Equal("00 80 0B 02 01 01 10 02 FF 00 00 00", HexOf(frames[1], 12));
            Assert.Equal("00 80 0B 02 02 04 10 01 80 01 02 03", HexOf(frames[3], 12));
            // The RGB header has no Spectrum, and takes its Static at the color it read.
            Assert.Equal("00 80 01 03", HexOf(frames[8], 4));
            Assert.Equal("00 80 04 05 10 00 FF 07 08 09", HexOf(frames[9], 10));
            Assert.Equal(CoolerMasterLighting.ArgbDirect, session.States[0].Mode);
        }

        [Fact]
        public void ASmallArgbFoundInDirect_IsHandedBackItsSpectrum()
        {
            var session = new CoolerMasterLighting.SmallArgbSession(
                new CoolerMasterLighting.ArgbPortState(CoolerMasterLighting.SmallDirect, 0x03, 0x00, true, 0x070809), "fake");
            var frames = session.HandBackFrames;
            Assert.Equal("00 80 01 01 00 02", HexOf(frames[0], 6));
            Assert.Equal("00 80 0B 02 01 01 02 10 FF 00 00 00", HexOf(frames[1], 12));
            var kept = new CoolerMasterLighting.SmallArgbSession(new CoolerMasterLighting.ArgbPortState(0x04, 0x02, 0x90, false, 0x0A0B0C), "fake");
            Assert.Equal("00 80 0B 02 01 04 02 10 90 0A 0B 0C", HexOf(kept.HandBackFrames[1], 12));
        }

        /// <summary>The Radeon pair's registration names no usage page, and every frame is a
        /// 65-byte output report (CMR6000Controller.cpp:102-214).</summary>
        [Theory]
        [InlineData(0x014D)]
        [InlineData(CoolerMasterLighting.Radeon6900)]
        public void TheRadeonCards_TakeOnlyACollectionWithTheirOutputReport(int product)
        {
            var family = new CoolerMasterLighting();
            var r = Reg(CoolerMasterLighting.OpenRgbFamily, product);
            var collection = CollectionOf(r, output: 65);
            Assert.NotNull(family.Match(collection));
            Assert.Null(family.Match(collection with { OutputReportLength = 64 }));
            Assert.Null(family.Match(collection with { OutputReportLength = 0 }));
        }

        /// <summary>A port whose generation read misses is a Gen1 strip of 6, as SetupZones treats
        /// any port that is no valid Gen2 port (RGBController_CMARGBGen2A1Controller.cpp:1333-1404).
        /// A controller where no port answers still gets no session.</summary>
        [Fact]
        public void AnA1PortWhoseGenerationIsNotRead_IsAStripOfSix()
        {
            var device = new FakeA1Device
            {
                Answer = report => (report[2], report[3], report[4]) switch
                {
                    (0x0B, 0x01, 0x01) => new byte[] { 0x80, 0x0B, 0x03, 0x01, 0x01 },
                    (0x0B, 0x01, 0x04) => new byte[] { 0x80, 0x0B, 0x03, 0x04, 0x01 },
                    (0x06, 0x01, 0x01) => new byte[] { 0x80, 0x06, 0x03, 0x01, 0x02, 30 },
                    _ => null,
                },
            };
            var targets = CoolerMasterLighting.A1Topology(device, 3);
            Assert.Equal(new[] { (0x01, 30), (0x02, 6), (0x04, 6) }, targets.Select(t => ((int)t.Port, t.Leds)));
            Assert.All(targets, t => Assert.Equal(0, t.Subport));

            Assert.Empty(CoolerMasterLighting.A1Topology(new FakeA1Device(), 3));
        }

        // ── HyperX ──

        private static HidCollectionInfo HyperXRow(OpenRgbRegistration r, ushort feature, ushort output) => new()
        {
            Path = $@"\\?\hid#vid_{r.VendorId:x4}&pid_{r.ProductId:x4}&mi_{r.Interface:x2}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
            VendorId = (ushort)r.VendorId,
            ProductId = (ushort)r.ProductId,
            UsagePage = r.UsagePage == OpenRgbRegistration.Any ? (ushort)0xFF00 : (ushort)r.UsagePage,
            Usage = 1,
            InterfaceNumber = r.Interface,
            FeatureReportLength = feature,
            OutputReportLength = output,
        };

        /// <summary>The length each protocol sends: 264-byte feature reports on the Alloy Elite and
        /// FPS (HyperXAlloyEliteController.cpp:266, HyperXAlloyFPSController.cpp:135), 65-byte
        /// feature reports on the Elite 2 and Origins (HyperXAlloyElite2Controller.cpp:182,
        /// HyperXAlloyOriginsController.cpp:115), 65-byte output reports on the Core, Eve and
        /// Origins 2 65 (HyperXAlloyOriginsCoreController.cpp:89, HyperXEve1800Controller.cpp:61).</summary>
        [Fact]
        public void EveryHyperXKeyboard_TakesOnlyACollectionWithItsReportLength()
        {
            var family = new HyperXKeyboardLighting();
            int seen = 0;
            foreach (var r in OpenRgbRegistrations.Of(HyperXKeyboardLighting.OpenRgbFamily))
            {
                if (HyperXKeyboardLighting.ProtocolOf(r.Detector) is not { } protocol) continue;
                if (family.Match(HyperXRow(r, 264, 65)) == null) continue;
                seen++;
                switch (protocol)
                {
                    case HyperXKeyboardLighting.Protocol.Elite:
                    case HyperXKeyboardLighting.Protocol.Fps:
                        Assert.Null(family.Match(HyperXRow(r, 263, 65)));
                        break;
                    case HyperXKeyboardLighting.Protocol.OriginsCore:
                    case HyperXKeyboardLighting.Protocol.Eve1800:
                    case HyperXKeyboardLighting.Protocol.Origins2_65:
                        Assert.NotNull(family.Match(HyperXRow(r, 0, 65)));
                        Assert.Null(family.Match(HyperXRow(r, 264, 64)));
                        break;
                    default:
                        Assert.NotNull(family.Match(HyperXRow(r, 65, 0)));
                        Assert.Null(family.Match(HyperXRow(r, 64, 65)));
                        break;
                }
            }
            Assert.True(seen > 10, seen.ToString());
        }

        // ── EVGA X20 ──

        /// <summary>Unleash keeps a mode per light, and OpenRGB's one mode byte is its own limit
        /// (EVGAMouseController.cpp:195-199). Each light goes back with its own, and only a
        /// breathing light takes the second frame that activates it.</summary>
        [Fact]
        public void TheX20_HandsEachLightBackInItsOwnMode()
        {
            var io = new LoggingHidChannel();
            foreach (var (led, mode) in new[] { (0, EvgaLighting.MouseBreathing), (1, (byte)3), (2, EvgaLighting.MouseStatic) })
            {
                var answer = LaptopFamilyTest.Padded("00 00 00 00 1D 02 80 02", 65);
                answer[8] = (byte)led;
                answer[9] = mode;
                answer[10] = 0x40;
                answer[14] = 1;
                answer[15] = 0x10;
                io.Answers.Enqueue(answer);
            }
            io.Echo = _ => new byte[65];
            var session = (EvgaLighting.MouseSession)EvgaLighting.MouseSession.Begin(io, io.Wait, 0);
            var colors = session.RestoreFrames().Skip(3).Select(f => (Led: f[8], Mode: f[9], Activate: f[1])).ToList();
            Assert.Equal(new (byte, byte, byte)[]
            {
                (0, EvgaLighting.MouseBreathing, 0x00),
                (0, EvgaLighting.MouseBreathing, 0xA1),
                (1, 3, 0x00),
                (2, EvgaLighting.MouseStatic, 0x00),
            }, colors.Select(c => (c.Led, c.Mode, c.Activate)));
        }
    }
}
