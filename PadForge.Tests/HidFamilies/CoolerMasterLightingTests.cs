using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A Cooler Master device on a scripted channel: every output
    /// report is logged, and each read hands back what
    /// <see cref="Answer"/> gives for the last report written, with report
    /// ID 0 in front as Windows delivers it.</summary>
    internal sealed class CoolerMasterDevice
    {
        public readonly List<byte[]> Writes = new();
        public readonly FakeReportChannel Channel;

        /// <summary>The answer's bytes after the report ID, as hidapi hands
        /// them over, for a report written, or null for no answer in
        /// time.</summary>
        public Func<byte[], byte[]> Answer = report => report.Skip(1).ToArray();

        public CoolerMasterDevice()
        {
            Channel = new FakeReportChannel
            {
                Path = @"\\?\hid#vid_2516&pid_0067&mi_01#fake",
                InputReportLength = 65,
                OutputReportLength = 65,
                FeatureReportLength = 0,
            };
            Channel.Input = () =>
            {
                var last = Channel.Calls.LastOrDefault(c => c.Kind == "out").Bytes;
                var answer = last == null ? null : Answer(last);
                if (answer == null) return null;
                var report = new byte[65];
                answer.AsSpan(0, Math.Min(64, answer.Length)).CopyTo(report.AsSpan(1));
                return report;
            };
        }

        public List<byte[]> Outs => Channel.Calls.Where(c => c.Kind == "out").Select(c => c.Bytes).ToList();
        public int Reads => Channel.Calls.Count(c => c.Kind == "in");
        public void Clear() => Channel.Calls.Clear();
    }

    /// <summary>The Cooler Master family against OpenRGB's
    /// CoolerMasterController at the pinned commit, libcmmk and SignalRGB's
    /// plugins (#557). Every expected frame is written out here from the
    /// reference, not built by the code under test.</summary>
    public class CoolerMasterLightingTests
    {
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

        /// <summary>A 65-byte frame: report ID 0, the given bytes, zeros.</summary>
        private static byte[] F(params byte[] payload)
        {
            var frame = new byte[65];
            payload.CopyTo(frame, 1);
            return frame;
        }

        private static void Same(byte[] expected, byte[] actual) => Assert.Equal(Hex(expected), Hex(actual));

        private static void Same(IReadOnlyList<byte[]> expected, IReadOnlyList<byte[]> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++) Same(expected[i], actual[i]);
        }

        private static HidCollectionInfo Collection(OpenRgbRegistration r)
        {
            int iface = r.Interface == OpenRgbRegistration.Any ? 0 : r.Interface;
            return new HidCollectionInfo
            {
                Path = $@"\\?\hid#vid_{r.VendorId:x4}&pid_{r.ProductId:x4}&mi_{iface:x2}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = (ushort)r.VendorId,
                ProductId = (ushort)r.ProductId,
                UsagePage = r.UsagePage == OpenRgbRegistration.Any ? (ushort)0xFF00 : (ushort)r.UsagePage,
                Usage = r.Usage == OpenRgbRegistration.Any ? (ushort)1 : (ushort)r.Usage,
                InterfaceNumber = iface,
                InputReportLength = 65,
                OutputReportLength = 65,
            };
        }

        private static HidLightingModel ModelOf(ushort pid)
        {
            var r = OpenRgbRegistrations.Of(CoolerMasterLighting.OpenRgbFamily).First(x => x.ProductId == pid);
            return CoolerMasterLighting.Instance.Match(Collection(r));
        }

        private static readonly Action<int> NoWait = _ => { };

        // ── Registrations ──

        /// <summary>The registrations OpenRGB makes no controller PadForge can
        /// drive for, each with its reason in the family's class
        /// comment.</summary>
        private static readonly Dictionary<string, string> Unlit = new()
        {
            ["Cooler Master MK850"] = "no case in DetectCoolerMasterV2Keyboards",
            ["Cooler Master SK620 White"] = "no case in DetectCoolerMasterV2Keyboards",
            ["Cooler Master SK620 Black"] = "no case in DetectCoolerMasterV2Keyboards",
            ["Cooler Master ARGB"] = "firmware named by the serial string",
            ["Cooler Master Small ARGB"] = "firmware named by the serial string",
            ["Cooler Master ARGB Gen 2 A1"] = "not ported",
            ["Cooler Master ARGB Gen 2 A1 V2"] = "not ported",
            ["Cooler Master ARGB Gen 2 A1 Mini"] = "not ported",
        };

        [Fact]
        public void EveryRegistration_IsMatchedByOpenRgbsRules_OrListedAsUnlit()
        {
            var registrations = OpenRgbRegistrations.Of(CoolerMasterLighting.OpenRgbFamily).ToList();
            // CoolerMasterControllerDetect.cpp:389-451, MM531 commented out
            // at line 422.
            Assert.Equal(37, registrations.Count);
            Assert.DoesNotContain(registrations, r => r.Name == "Cooler Master MM531");
            foreach (var r in registrations)
            {
                var model = CoolerMasterLighting.Instance.Match(Collection(r));
                if (Unlit.ContainsKey(r.Name))
                {
                    Assert.True(model == null, r.Name);
                    continue;
                }
                Assert.True(model != null, r.Name);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(CoolerMasterLighting.Instance, model.Family);
                Assert.Equal(new[] { VendorPrograms.MasterPlus }, model.Programs);
                Assert.Equal($"coolermaster:2516:{r.ProductId:X4}", model.Map.Product);
                Assert.True(model.Map.Lights.Count > 0, r.Name);
                // The same model for the same collection.
                Assert.Same(model, CoolerMasterLighting.Instance.Match(Collection(r)));
                // Through the family list too.
                Assert.Same(model, HidLightingFamilies.Match(Collection(r)));
            }
            Assert.Equal(Unlit.Count, registrations.Count(r => Unlit.ContainsKey(r.Name)));
        }

        [Fact]
        public void AnotherInterfaceOrVendor_IsNotMatched()
        {
            var r = OpenRgbRegistrations.Of(CoolerMasterLighting.OpenRgbFamily).First(x => x.ProductId == 0x0067);
            Assert.Null(CoolerMasterLighting.Instance.Match(Collection(r) with { InterfaceNumber = 0, Path = Collection(r).Path.Replace("mi_01", "mi_00") }));
            Assert.Null(CoolerMasterLighting.Instance.Match(Collection(r) with { UsagePage = 0x0001 }));
            Assert.Null(CoolerMasterLighting.Instance.Match(Collection(r) with { VendorId = 0x1234 }));
            Assert.Null(CoolerMasterLighting.Instance.Match(null));
        }

        [Fact]
        public void MasterPlus_IsAProgramOfTheTable()
        {
            Assert.Contains(VendorPrograms.MasterPlus, VendorPrograms.All);
            Assert.Equal(1, VendorPrograms.All.Count(p => ReferenceEquals(p, VendorPrograms.MasterPlus)));
            Assert.Equal("MasterPlus", VendorPrograms.MasterPlus.Name);
            Assert.True(VendorPrograms.MasterPlus.Owns("MasterPlus(PER. Only)"));
            Assert.True(VendorPrograms.MasterPlus.Owns("cm-blackhawk"));
            Assert.False(VendorPrograms.MasterPlus.Owns("CMService"));
        }

        // ── Keyboards: light maps ──

        [Fact]
        public void EveryKeyboard_HasOpenRgbsLayoutInEachRegion_AddressedByItsLedValue()
        {
            foreach (ushort pid in CoolerMasterLighting.V1Keyboards.Concat(CoolerMasterLighting.V2Keyboards))
            {
                var row = KeyboardLayouts.ModelsOf("CoolerMaster", pid).Single();
                foreach (var region in new[] { KeyboardLayout.AnsiQwerty, KeyboardLayout.IsoQwerty, KeyboardLayout.Jis })
                {
                    var keys = KeyboardLayouts.Build(row.Recipe, region).Keys;
                    var lights = CoolerMasterLighting.KeyboardLights(pid, region);
                    Assert.Equal(keys.Count, lights.Count);
                    for (int i = 0; i < keys.Count; i++)
                    {
                        Assert.Equal((int)keys[i].Value, lights[i].Address);
                        OpenRgbKeys.TryGet(keys[i].Name, out ushort usage, out _);
                        Assert.Equal(usage, lights[i].Usage);
                        Assert.InRange(lights[i].Address, 0, CoolerMasterLighting.MaxLeds - 1);
                    }
                }
                Assert.Equal(CoolerMasterLighting.KeyboardLights(pid, KeyboardLayout.AnsiQwerty).Count,
                    ModelOf(pid).Map.Lights.Count);
            }
        }

        [Fact]
        public void TheProS_EscapeAndF1_SitAtOpenRgbsValues()
        {
            // mk_pro_s_keymap: ESC 96, F1 97 (CMKeyboardDevices.cpp:21-22).
            var lights = CoolerMasterLighting.KeyboardLights(CoolerMasterLighting.ProS, KeyboardLayout.AnsiQwerty);
            Assert.Equal(96, lights.Single(l => l.Usage == 0x29).Address);
            Assert.Equal(97, lights.Single(l => l.Usage == 0x3A).Address);
            // mk_pro_l_keymap: ESC 11 (line 39).
            lights = CoolerMasterLighting.KeyboardLights(CoolerMasterLighting.ProL, KeyboardLayout.AnsiQwerty);
            Assert.Equal(11, lights.Single(l => l.Usage == 0x29).Address);
        }

        // ── Keyboards: frames ──

        [Fact]
        public void TheControlFrames_AreOpenRgbsAndLibcmmks()
        {
            Same(F(0x41, 0x00), CoolerMasterLighting.ControlMode(0x00));
            Same(F(0x41, 0x02), CoolerMasterLighting.ControlMode(CoolerMasterLighting.V1Manual));
            Same(F(0x41, 0x05), CoolerMasterLighting.ControlMode(CoolerMasterLighting.V2Manual));
            Same(F(0x51, 0x00, 0x00, 0x00, 0x07), CoolerMasterLighting.ActiveProfile(7));
            Same(F(0x52, 0x00), CoolerMasterLighting.ActiveProfileQuery());
            Same(F(0x01, 0x02), CoolerMasterLighting.V1FirmwareQuery());
            Same(F(0x12, 0x20), CoolerMasterLighting.V2FirmwareQuery());
            Same(F(0x51, 0x28, 0x00, 0x00, 0xFF), CoolerMasterLighting.V2Apply());
        }

        [Fact]
        public void V1Colors_AreEightFramesOfSixteen()
        {
            var map = new int[CoolerMasterLighting.MaxLeds];
            map[0] = 0xFF0000;
            map[16] = 0x00FF00;
            map[127] = 0x0000FF;
            map[128] = 0xFFFFFF; // past what the eight frames carry
            var frames = CoolerMasterLighting.V1ColorFrames(map);
            Assert.Equal(8, frames.Count);
            // C0 02 (i * 2) 00, then 16 colors (CMKeyboardV1Controller.cpp:393-411).
            Same(F(0xC0, 0x02, 0x00, 0x00, 0xFF, 0x00, 0x00), frames[0]);
            Same(F(0xC0, 0x02, 0x02, 0x00, 0x00, 0xFF, 0x00), frames[1]);
            var last = F(0xC0, 0x02, 0x0E, 0x00);
            last[5 + 45] = 0x00;
            last[5 + 46] = 0x00;
            last[5 + 47] = 0xFF;
            Same(last, frames[7]);
            for (int i = 2; i < 7; i++) Same(F(0xC0, 0x02, (byte)(i * 2), 0x00), frames[i]);
        }

        [Fact]
        public void V2Colors_AreTenFramesOfTheLinearMap()
        {
            var map = new int[CoolerMasterLighting.MaxLeds];
            map[0] = 0x010203;
            map[13] = 0x112233; // bytes 39-41: the first frame's last, the second's first two
            map[193] = 0xAABBCC; // byte 579, the last the frames carry
            var frames = CoolerMasterLighting.V2ColorFrames(map);
            Assert.Equal(10, frames.Count);
            // CMKeyboardV2Controller.cpp:237-248.
            var first = F(0x56, 0x83, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
                0x80, 0x01, 0x00, 0xC1, 0x00, 0x00, 0x00, 0x00,
                0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00,
                0x01, 0x02, 0x03);
            first[64] = 0x11;
            Same(first, frames[0]);
            Same(F(0x56, 0x83, 0x01, 0x00, 0x22, 0x33), frames[1]);
            // Byte 579 is the 60th of the ninth frame (40 + 8 * 60 + 59).
            var ninth = F(0x56, 0x83, 0x09, 0x00);
            ninth[5 + 59] = 0xAA;
            Same(ninth, frames[9]);
        }

        [Fact]
        public void V2Initialize_IsEachModelsSequence()
        {
            byte[] Magic(byte p) => F(0x56, 0x81, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, p, 0x00, 0x00, 0x00, 0xBB, 0xBB, 0xBB, 0xBB);
            var startup = new[]
            {
                F(0x12), F(0x12, 0x20), F(0x12, 0x01), F(0x12, 0x22),
                F(0x42, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0x01), F(0x43, 0x00, 0x00, 0x00, 0x01),
            };
            // CMKeyboardV2Controller.cpp:109-163.
            Same(startup.Append(Magic(0x09)).ToList(), CoolerMasterLighting.V2Initialize(0x0149));
            Same(startup.Append(Magic(0x09)).ToList(), CoolerMasterLighting.V2Initialize(0x014B));
            Same(new[] { F(0x51, 0x00, 0x00, 0x00, 0x0C) }, CoolerMasterLighting.V2Initialize(0x0089));
            Same(new[] { F(0x51, 0x00, 0x00, 0x00, 0x0C) }, CoolerMasterLighting.V2Initialize(0x008D));
            Same(startup.Append(Magic(0x0C)).ToList(), CoolerMasterLighting.V2Initialize(0x015D));
            Same(startup.Append(Magic(0x0C)).ToList(), CoolerMasterLighting.V2Initialize(0x01AB));
            Same(new[] { F(0x51, 0x00, 0x00, 0x00, 0x05), Magic(0x0A) }, CoolerMasterLighting.V2Initialize(0x009F));
            Same(new[] { F(0x51, 0x00, 0x00, 0x00, 0x05), Magic(0x0A) }, CoolerMasterLighting.V2Initialize(0x0147));
            Same(new[] { F(0x51, 0x00, 0x00, 0x00, 0x05), Magic(0x09) }, CoolerMasterLighting.V2Initialize(0x0145));
            Same(new[] { F(0x51, 0x00, 0x00, 0x00, 0x05), Magic(0x0A) }, CoolerMasterLighting.V2Initialize(0x008F));
            Same(new[] { F(0x41, 0x80), Magic(0x01) }, CoolerMasterLighting.V2Initialize(0x007F));
            Same(new[] { F(0x56, 0x81, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0xBB, 0xBB, 0xBB) },
                CoolerMasterLighting.V2Initialize(0x0067));
        }

        [Fact]
        public void TheFirmwareStrings_NameTheRegion()
        {
            // "<layout>.<minor>.<major>" on V1 (CMKeyboardV1Controller.cpp:429-500).
            var v1 = new byte[64];
            Encoding.ASCII.GetBytes("2.2.1").CopyTo(v1, 4);
            Assert.Equal("2.2.1", CoolerMasterLighting.V1Firmware(v1));
            Assert.Equal(KeyboardLayout.IsoQwerty, CoolerMasterLighting.RegionOf("2.2.1", 0));
            Assert.Equal(KeyboardLayout.AnsiQwerty, CoolerMasterLighting.RegionOf("1.2.1", 0));
            Assert.Equal(KeyboardLayout.Jis, CoolerMasterLighting.RegionOf("3.0.0", 0));
            Assert.Equal(KeyboardLayout.AnsiQwerty, CoolerMasterLighting.RegionOf("0.0.0", 0));
            Assert.Equal(KeyboardLayout.AnsiQwerty, CoolerMasterLighting.RegionOf("", 0));
            // "V<layout>.<minor>.<major>" in UTF-16 from byte 8 on V2
            // (CMKeyboardV2Controller.cpp:29-107).
            var v2 = new byte[64];
            Encoding.Unicode.GetBytes("V3.01.02").CopyTo(v2, 8);
            Assert.Equal("V3.01.02", CoolerMasterLighting.V2Firmware(v2));
            Assert.Equal(KeyboardLayout.Jis, CoolerMasterLighting.RegionOf("V3.01.02", 1));
            Assert.Equal(KeyboardLayout.IsoQwerty, CoolerMasterLighting.RegionOf("V2.01.02", 1));
            Assert.Equal(KeyboardLayout.AnsiQwerty, CoolerMasterLighting.RegionOf("V1.01.02", 1));
        }

        [Fact]
        public void AV1Session_ReadsTheFirmware_PaintsAndHandsBackToTheFirmware()
        {
            var device = new CoolerMasterDevice();
            device.Answer = report =>
            {
                var answer = report.Skip(1).ToArray();
                if (report[1] == 0x01 && report[2] == 0x02) Encoding.ASCII.GetBytes("2.2.1\0").CopyTo(answer, 4);
                return answer;
            };
            var model = ModelOf(CoolerMasterLighting.ProS);
            var session = Assert.IsType<CoolerMasterLighting.KeyboardSession>(model.Begin(device.Channel, NoWait));
            Same(new[] { F(0x41, 0x00), F(0x01, 0x02) }, device.Outs);
            Assert.Equal(2, device.Reads);
            Assert.True(session.V1);
            Assert.Equal(KeyboardLayout.IsoQwerty, session.Region);
            Assert.NotNull(session.Map);
            Assert.Equal(CoolerMasterLighting.KeyboardLights(CoolerMasterLighting.ProS, KeyboardLayout.IsoQwerty).Count,
                session.Map.Lights.Count);
            Assert.Empty(session.CrashFrames);

            device.Clear();
            var lights = session.Map.Lights;
            var colors = lights.Select(l => l.Usage == 0x29 ? 0x123456 : 0).ToArray();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device.Channel, NoWait, lights, colors));
            var outs = device.Outs;
            Assert.Equal(9, outs.Count);
            Same(F(0x41, 0x02), outs[0]);
            // Escape's LED is 96: frame 6 (C0 02 0C 00), its first color.
            Same(F(0xC0, 0x02, 0x0C, 0x00, 0x12, 0x34, 0x56), outs[7]);
            Assert.Equal(9, device.Reads);
            Same(new[] { F(0x41, 0x00) }, session.CrashFrames);

            device.Clear();
            Assert.True(session.HandBack(device.Channel, NoWait));
            Same(new[] { F(0x41, 0x00) }, device.Outs);
            Assert.Empty(session.CrashFrames);
            device.Clear();
            Assert.True(session.HandBack(device.Channel, NoWait));
            Assert.Empty(device.Outs);
        }

        [Fact]
        public void AV2Session_ReadsTheProfile_AndSetsItBackBeforeTheFirmwareTakesOver()
        {
            var device = new CoolerMasterDevice();
            device.Answer = report =>
            {
                var answer = report.Skip(1).ToArray();
                if (report[1] == 0x12 && report[2] == 0x20) Encoding.Unicode.GetBytes("V1.02.03").CopyTo(answer, 8);
                if (report[1] == 0x52 && report[2] == 0x00) answer[4] = 0x03;
                return answer;
            };
            var model = ModelOf(CoolerMasterLighting.Sk630);
            var session = Assert.IsType<CoolerMasterLighting.KeyboardSession>(model.Begin(device.Channel, NoWait));
            Same(new[] { F(0x41, 0x00), F(0x12, 0x20), F(0x52, 0x00) }, device.Outs);
            Assert.Equal((byte)3, session.Profile);
            Assert.Null(session.Map); // ANSI: the model's map.

            device.Clear();
            var lights = model.Map.Lights;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device.Channel, NoWait, lights, new int[lights.Count]));
            var outs = device.Outs;
            // 41 05, the SK630's 51 00 00 00 0C, ten color frames, the apply.
            Assert.Equal(13, outs.Count);
            Same(F(0x41, 0x05), outs[0]);
            Same(F(0x51, 0x00, 0x00, 0x00, 0x0C), outs[1]);
            Same(F(0x51, 0x28, 0x00, 0x00, 0xFF), outs[12]);
            Assert.Equal(13, device.Reads);

            device.Clear();
            Assert.True(session.HandBack(device.Channel, NoWait));
            Same(new[] { F(0x51, 0x00, 0x00, 0x00, 0x03), F(0x41, 0x00) }, device.Outs);
        }

        [Fact]
        public void AKeyboardThatDoesNotAnswer_GetsNoSession_AndAPictureThatGoesUnanswered_Fails()
        {
            var device = new CoolerMasterDevice { Answer = _ => null };
            Assert.Null(ModelOf(CoolerMasterLighting.ProS).Begin(device.Channel, NoWait));
            Same(new[] { F(0x41, 0x00) }, device.Outs);

            // A profile answer of another kind: no profile to set back.
            device = new CoolerMasterDevice();
            device.Answer = report => report[1] == 0x52 ? new byte[] { 0x52, 0x01, 0, 0, 9 } : report.Skip(1).ToArray();
            var session = (CoolerMasterLighting.KeyboardSession)ModelOf(CoolerMasterLighting.Mk750).Begin(device.Channel, NoWait);
            Assert.Null(session.Profile);
            device.Answer = _ => null;
            var lights = ModelOf(CoolerMasterLighting.Mk750).Map.Lights;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(device.Channel, NoWait, lights, new int[lights.Count]));
            // Owed from the first write on.
            Same(new[] { F(0x41, 0x00) }, session.CrashFrames);
        }

        // ── Mice ──

        [Fact]
        public void AnMm530_EntersTheCustomMode_PaintsWheelButtonsAndLogo_AndPutsTheModeBack()
        {
            var device = new CoolerMasterDevice();
            device.Answer = report =>
            {
                var answer = report.Skip(1).ToArray();
                if (report[1] == 0x52 && report[2] == 0x28) answer[4] = 0x01; // breathing
                if (report[1] == 0x52 && report[2] == 0xA8)
                    for (int i = 4; i < 13; i++) answer[i] = (byte)(0x10 + i);
                return answer;
            };
            var model = ModelOf(CoolerMasterLighting.Mm530);
            Assert.Equal(3, model.Map.Lights.Count);
            var session = Assert.IsType<CoolerMasterLighting.MouseSession>(model.Begin(device.Channel, NoWait));
            // CMMMController.cpp:63-66.
            Same(new[] { F(0x41, 0x80), F(0x52, 0x2C), F(0x52, 0xA8), F(0x52, 0x28) }, device.Outs);

            device.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device.Channel, NoWait, model.Map.Lights,
                new[] { 0x112233, 0x445566, 0x778899 }));
            var parameters = F(0x51, 0x2C, 0x00, 0x00, 0xB0, 0x00, 0x20, 0xFF, 0xFF, 0xFF);
            for (int i = 17; i < 65; i++) parameters[i] = 0xFF;
            // Wheel at 11-13, buttons at 5-7, logo at 8-10 (CMMMController.cpp:30-35, 59-61).
            var colors = F(0x51, 0xA8, 0x00, 0x00, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0x11, 0x22, 0x33);
            Same(new[] { F(0x51, 0x30), parameters, F(0x51, 0x28, 0x00, 0x00, 0xB0), colors }, device.Outs);

            device.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device.Channel, NoWait, model.Map.Lights,
                new[] { 0x112233, 0x445566, 0x778899 }));
            Same(new[] { colors }, device.Outs);

            device.Clear();
            Assert.True(session.HandBack(device.Channel, NoWait));
            var custom = F(0x51, 0xA8, 0x00, 0x00, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C);
            Same(new[] { custom, F(0x41, 0x80), F(0x51, 0x28, 0x00, 0x00, 0x01) }, device.Outs);
        }

        [Fact]
        public void AnMm730_PutsTheWheelAt5_TheLogoAt8_AndCustomModesBackByZones()
        {
            Same(F(0x51, 0xA8, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66),
                CoolerMasterLighting.MmColors(false, 0x112233, 0, 0x445566));
            var custom = new byte[16];
            var back = CoolerMasterLighting.MmHandBack(custom, 0xE0);
            Same(new[] { F(0x51, 0xA8), F(0x51, 0x30, 0x00, 0x00, 0x01), F(0x51, 0x28, 0x00, 0x00, 0xE0) }, back);
            back = CoolerMasterLighting.MmHandBack(custom, 0xB0);
            Same(new[] { F(0x51, 0xA8), F(0x51, 0x30), F(0x51, 0x28, 0x00, 0x00, 0xB0) }, back);
            Assert.Equal(2, ModelOf(0x0165).Map.Lights.Count);
            Assert.Equal(2, ModelOf(0x0141).Map.Lights.Count);
        }

        [Fact]
        public void AMouseWhoseModeIsNotAnswered_GetsNoSession()
        {
            var device = new CoolerMasterDevice();
            device.Answer = report => report[1] == 0x52 && report[2] == 0x28 ? null : report.Skip(1).ToArray();
            Assert.Null(ModelOf(CoolerMasterLighting.Mm530).Begin(device.Channel, NoWait));
            // A custom status that never carries A8 at byte 1.
            device.Answer = report => report[1] == 0x52 && report[2] == 0xA8 ? new byte[64] : report.Skip(1).ToArray();
            Assert.Null(ModelOf(CoolerMasterLighting.Mm530).Begin(device.Channel, NoWait));
        }

        [Fact]
        public void AnMm711_SendsSignalRgbsThreeFrames_AndPutsItsModeBack()
        {
            var device = new CoolerMasterDevice();
            device.Answer = report =>
            {
                var answer = report.Skip(1).ToArray();
                if (report[1] == 0x52 && report[2] == 0x28) answer[4] = 0x02;
                return answer;
            };
            var model = ModelOf(0x0101);
            var session = model.Begin(device.Channel, NoWait);
            Same(new[] { F(0x41, 0x80), F(0x52, 0x2B), F(0x52, 0xA8), F(0x52, 0x28) }, device.Outs);
            device.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device.Channel, NoWait, model.Map.Lights,
                new[] { 0x010203, 0x040506 }));
            // signal-plugins CoolerMaster_MM711_Mouse.js:160-171, the colors at
            // CMMM711Controller.cpp:149-154.
            Same(new[]
            {
                F(0x41, 0x80), F(0x51, 0xA8, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06),
                F(0x51, 0x28, 0x00, 0x00, 0xB0),
            }, device.Outs);
            device.Clear();
            Assert.True(session.HandBack(device.Channel, NoWait));
            Same(new[] { F(0x51, 0xA8), F(0x41, 0x80), F(0x51, 0x28, 0x00, 0x00, 0x02) }, device.Outs);
        }

        [Fact]
        public void AnMm712_GoesDirect_AndLeavesTheDirectStateForItsMode()
        {
            var device = new CoolerMasterDevice();
            device.Answer = report => report[1] == 0x4C && report[3] == 0x07
                ? new byte[] { 0x4D, 0x81, 0x07, 0x01 }
                : report[1] == 0x5A ? null : report.Skip(1).ToArray();
            var model = ModelOf(0x0169);
            var session = model.Begin(device.Channel, NoWait);
            Same(new[] { F(0x44, 0x81, 0x02), F(0x4C, 0x81, 0x07) }, device.Outs);
            device.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device.Channel, NoWait, model.Map.Lights, new[] { 0xFF0025 }));
            // CMMM712Controller.cpp:114-130 and MM712protocol.txt.
            Same(new[] { F(0x5A, 0x81, 0x02), F(0x5A, 0x81, 0x03, 0xFF, 0x00, 0x25) }, device.Outs);
            Assert.Equal(0, device.Reads);
            device.Clear();
            Assert.True(session.HandBack(device.Channel, NoWait));
            Same(new[] { F(0x5A, 0x81, 0x01), F(0x44, 0x81, 0x02), F(0x4C, 0x81, 0x08, 0x01) }, device.Outs);
            Assert.Equal(2, device.Reads);
            // A mode answer of another kind starts nothing.
            device.Answer = report => report.Skip(1).ToArray();
            Assert.Null(model.Begin(device.Channel, NoWait));
        }

        // ── Mousepads ──

        [Fact]
        public void AnMp750_PaintsTheStaticMode_AndSendsTheStateItReadBack()
        {
            var device = new CoolerMasterDevice();
            // 80 05, breathing, a color, speed byte 0x90.
            device.Answer = report => report[1] == 0x07 ? new byte[] { 0x80, 0x05, 0x03, 0x10, 0x20, 0x30, 0x90 } : null;
            var model = ModelOf(0x0107);
            var session = Assert.IsType<CoolerMasterLighting.Mp750Session>(model.Begin(device.Channel, NoWait));
            Same(new[] { F(0x07) }, device.Outs);
            // 0xA0 is the last of FF E0 C0 A0 80 ... at least 0x90: place 3.
            Assert.Equal(new CoolerMasterLighting.Mp750State(0x03, 0x102030, 3), session.State);
            device.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device.Channel, NoWait, model.Map.Lights, new[] { 0xAABBCC }));
            // Static: 01 04, the color, speed_mode_data[0] (CMMP750Controller.cpp:15-28, 151-176).
            Same(new[] { F(0x01, 0x04, 0xAA, 0xBB, 0xCC, 0xFF) }, device.Outs);
            Assert.Equal(0, device.Reads);
            device.Clear();
            Assert.True(session.HandBack(device.Channel, NoWait));
            Same(new[] { F(0x03, 0x04, 0x10, 0x20, 0x30, 0xA0) }, device.Outs);
            // A cycle mode carries its speed at byte 3.
            Same(F(0x04, 0x04, 0x40), CoolerMasterLighting.Mp750Frame(new(0x04, 0x123456, 6)));
            // A pad already static keeps its speed.
            var still = new CoolerMasterLighting.Mp750Session(new(0x01, 0, 8));
            Same(F(0x01, 0x04, 0x01, 0x02, 0x03, 0x00), still.PictureFrame(model.Map.Lights, new[] { 0x010203 }));
            // An answer that is not 80 05 starts nothing.
            device.Answer = _ => new byte[] { 0x81, 0x05 };
            Assert.Null(model.Begin(device.Channel, NoWait));
        }

        // ── Monitors and the desk ──

        [Fact]
        public void AMonitor_TurnsSoftwareModeOnOnce_SendsSevenFrames_AndTurnsItOff()
        {
            var device = new CoolerMasterDevice();
            var model = ModelOf(0x01BB);
            Assert.Equal(47, model.Map.Lights.Count);
            Assert.All(model.Map.Lights, l => Assert.Equal(LightKind.Edge, l.Kind));
            var session = model.Begin(device.Channel, NoWait);
            Assert.Empty(device.Outs);
            var colors = new int[47];
            colors[0] = 0x010203;
            colors[19] = 0x0A0B0C; // bytes 57-59: the first frame's last and the second's first two
            colors[46] = 0x0D0E0F; // bytes 138-140
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device.Channel, NoWait, model.Map.Lights, colors));
            var outs = device.Outs;
            Assert.Equal(8, outs.Count);
            Same(F(0x80, 0x07, 0x02, 0x02, 0x00, 0x01), outs[0]);
            var first = F(0x00, 0x07, 0x02, 0x02, 0x01, 0x80, 0x01, 0x02, 0x03);
            first[64] = 0x0A;
            Same(first, outs[1]);
            var second = F(0x01, 0x0B, 0x0C);
            // Byte 138 of the data is the 18th of the third frame (58 + 63 + 17).
            Same(second, outs[2]);
            Same(F(0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x0D, 0x0E, 0x0F), outs[3]);
            Same(F(0x86), outs[7]);
            Assert.Equal(0, device.Reads);
            device.Clear();
            session.Paint(device.Channel, NoWait, model.Map.Lights, colors);
            Assert.Equal(7, device.Outs.Count);
            device.Clear();
            Assert.True(session.HandBack(device.Channel, NoWait));
            Same(new[] { F(0x80, 0x07, 0x02, 0x02, 0x00, 0x00) }, device.Outs);
        }

        [Fact]
        public void TheDesk_LightsBothSides_AndHandsBothBack()
        {
            var device = new CoolerMasterDevice();
            var model = ModelOf(0x01A9);
            Assert.Equal(192, model.Map.Lights.Count);
            var session = model.Begin(device.Channel, NoWait);
            Assert.Empty(device.Outs); // no ResetDevice
            var colors = new int[192];
            colors[0] = 0x112233;
            colors[96] = 0x445566;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device.Channel, NoWait, model.Map.Lights, colors));
            var outs = device.Outs;
            Assert.Equal(16, outs.Count);
            // CMGD160Controller.cpp:103-190.
            Same(F(0x80, 0x07, 0x02, 0x01, 0x00, 0x01), outs[0]);
            Same(F(0x80, 0x07, 0x02, 0x02, 0x00, 0x01), outs[1]);
            Same(F(0x00, 0x07, 0x02, 0x01, 0x01, 0xFF, 0x11, 0x22, 0x33), outs[2]);
            Same(F(0x86), outs[8]);
            Same(F(0x00, 0x07, 0x02, 0x02, 0x01, 0xFF, 0x44, 0x55, 0x66), outs[9]);
            device.Clear();
            Assert.True(session.HandBack(device.Channel, NoWait));
            Same(new[] { F(0x80, 0x07, 0x02, 0x01, 0x00, 0x00), F(0x80, 0x07, 0x02, 0x02, 0x00, 0x00) }, device.Outs);
        }

        // ── Graphics cards ──

        [Fact]
        public void AGraphicsCard_GetsOpenRgbsDirectSequence_AndNoHandBack()
        {
            var device = new CoolerMasterDevice();
            var model = ModelOf(0x015B);
            var session = model.Begin(device.Channel, NoWait);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device.Channel, NoWait, model.Map.Lights, new[] { 0x102030 }));
            var mode = Enumerable.Repeat((byte)0xFF, 65).ToArray();
            mode[0] = 0x00;
            mode[1] = 0x51;
            mode[2] = 0x2C;
            mode[3] = 0x01;
            mode[4] = 0x00;
            mode[5] = 0x00;
            mode[7] = 0x00;
            mode[0x0B] = 0x10;
            mode[0x0C] = 0x20;
            mode[0x0D] = 0x30;
            mode[0x0E] = 0x00;
            mode[0x0F] = 0x00;
            mode[0x10] = 0x00;
            // CMR6000Controller.cpp:102-214.
            Same(new[]
            {
                F(0x41, 0x80), F(0x51, 0x9C, 0x01, 0x00, 0x10, 0x20, 0x30), mode,
                F(0x51, 0xA0, 0x01, 0x00, 0x00, 0x03, 0x00, 0x00, 0x05, 0x06), F(0x51, 0x28, 0x00, 0x00, 0xE0),
            }, device.Outs);
            Assert.Equal(4, device.Reads);
            Assert.Empty(session.CrashFrames);
            device.Clear();
            Assert.True(session.HandBack(device.Channel, NoWait));
            Assert.Empty(device.Outs);
            // The 6000 sends no second color.
            device.Clear();
            ModelOf(0x014D).Begin(device.Channel, NoWait).Paint(device.Channel, NoWait, model.Map.Lights, new[] { 0x102030 });
            Assert.Equal(4, device.Outs.Count);
        }

        // ── RGB controller ──

        [Fact]
        public void TheRgbController_ReadsWhatItPutsBack_PaintsItsCustomMode_AndRestores()
        {
            var device = new CoolerMasterDevice();
            device.Answer = report =>
            {
                var answer = report.Skip(1).ToArray();
                if (report[1] == 0x52 && report[2] == 0x28) answer[4] = 0x01; // breathing
                if (report[1] == 0x52 && report[2] == 0x2C)
                {
                    answer[5] = 0x30;
                    answer[9] = 0x80;
                    answer[10] = 0x01; answer[11] = 0x02; answer[12] = 0x03;
                    answer[13] = 0x04; answer[14] = 0x05; answer[15] = 0x06;
                }
                if (report[1] == 0x52 && report[2] == 0xA8)
                    for (int i = 4; i < 16; i++) answer[i] = (byte)(0xA0 + i);
                return answer;
            };
            var model = ModelOf(0x004F);
            Assert.Equal(150, model.HoldMs);
            Assert.Equal(500, model.RepaintMs);
            var session = Assert.IsType<CoolerMasterLighting.RgbcSession>(model.Begin(device.Channel, NoWait));
            Same(new[] { F(0x41, 0x01), F(0x52, 0x28), F(0x41, 0x00), F(0x52, 0x2C, 0x00, 0x00, 0x01), F(0x52, 0xA8) },
                device.Outs);
            Assert.Equal(0x01, session.State.Mode);
            Assert.Equal(0x30, session.State.Speed);
            Assert.Equal(0x80, session.State.Brightness);

            device.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(device.Channel, NoWait, model.Map.Lights,
                new[] { 0x111111, 0x222222, 0x333333, 0x444444 }));
            // CMRGBController.cpp:275-285, with SendSetConfig's frame at
            // lines 145-203.
            var config = F(0x51, 0x2C, 0x00, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x11, 0x11, 0x11);
            for (int i = 17; i < 65; i++) config[i] = 0xFF;
            Same(new[]
            {
                F(0x41, 0x80), F(0x51, 0x30),
                F(0x51, 0xA8, 0x00, 0x00, 0x11, 0x11, 0x11, 0x22, 0x22, 0x22, 0x33, 0x33, 0x33, 0x44, 0x44, 0x44),
                F(0x51, 0x28, 0x00, 0x00, 0x04), F(0x51, 0x30), config, F(0x50, 0x55), F(0x41, 0x00),
            }, device.Outs);

            device.Clear();
            Assert.True(session.HandBack(device.Channel, NoWait));
            var outs = device.Outs;
            Assert.Equal(13, outs.Count);
            Same(F(0x51, 0xA8, 0x00, 0x00, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF), outs[2]);
            var breathing = F(0x51, 0x2C, 0x00, 0x00, 0x01, 0x30, 0x20, 0xFF, 0xFF, 0x80, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06);
            for (int i = 17; i < 65; i++) breathing[i] = 0xFF;
            Same(new[] { F(0x41, 0x01), breathing, F(0x51, 0x28, 0x00, 0x00, 0x01), F(0x50, 0x55), F(0x41, 0x00) },
                outs.Skip(8).ToList());
        }

        [Fact]
        public void TheRgbController_ColorCycleAndOff_KeepOpenRgbsSpecialCases()
        {
            var cycle = CoolerMasterLighting.RgbcConfig(0x02, 0x80, 0x10, 0x123456, 0x654321);
            Assert.Equal(0xDF, cycle[10]);
            Same(new byte[] { 0xFF, 0xFF, 0xFF, 0, 0, 0 }, cycle.AsSpan(11, 6).ToArray());
            Assert.Equal(0x03, CoolerMasterLighting.RgbcConfig(0xFE, 0, 0x80, 0, 0)[10]);
            Assert.Equal(0x19, CoolerMasterLighting.RgbcConfig(0x03, 0, 0, 0, 0)[8]);
        }

        [Fact]
        public void TheRgbController_WithAnUnansweredRead_GetsNoSession()
        {
            var device = new CoolerMasterDevice();
            device.Answer = report => report[1] == 0x52 && report[2] == 0xA8 ? null : report.Skip(1).ToArray();
            Assert.Null(ModelOf(0x004F).Begin(device.Channel, NoWait));
            device.Answer = report => report[1] == 0x52 && report[2] == 0x2C ? new byte[] { 0x52, 0x2B } : report.Skip(1).ToArray();
            Assert.Null(ModelOf(0x004F).Begin(device.Channel, NoWait));
        }

        // ── hidapi's shape of an answer ──

        [Fact]
        public void AnAnswer_LosesReportIdZero_AsHidapiDropsIt()
        {
            Assert.Equal(new byte[] { 1, 2 }, CoolerMasterLighting.AsHidapi(new byte[] { 0, 1, 2 }, 3));
            Assert.Equal(new byte[] { 7, 1 }, CoolerMasterLighting.AsHidapi(new byte[] { 7, 1, 2 }, 2));
            Assert.Empty(CoolerMasterLighting.AsHidapi(new byte[] { 0, 1 }, 0));
        }
    }
}
