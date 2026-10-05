using System;
using System.Linq;
using System.Reflection;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Models2D;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Switch 2 Pro Controller on HIDMaestro 1.11.0: the composite
    /// persona with motion (switch2-pro-controller-composite, HIDMaestro#66)
    /// beside the plain profile, and the state both are handed.
    ///
    /// <para>The wire tests run HIDMaestro's own encoders off the SDK on
    /// disk: VendorBlobCodec for the plain profile's report 0x09 and
    /// Switch2ProPacker for the composite's state body. That is the
    /// consuming side's definition of every bit, the standard ValveWireTests
    /// set. A Switch 2 Pro slot once handed HIDMaestro its raw button mask,
    /// bit N for row N. The first Pro Controller's packer reads a mask that
    /// way and these two profiles read names, so no row of the 21 reached
    /// its own bit.</para>
    /// </summary>
    public class Switch2ProCompositeTests
    {
        private const string S1 = "switch-pro";
        private const string Plain = "switch2-pro-controller";
        private const string Full = "switch2-pro-controller-composite";

        private const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static readonly Assembly Sdk = typeof(HMContext).Assembly;

        private static HMProfile Profile(string id) =>
            HMaestroProfileCatalog.AllProfiles.First(p => p.Id == id);

        /// <summary>A controller that is never connected. Its constructor
        /// reads the profile and only stores the context, so the context is
        /// an empty shell: HMContext's own constructor starts the driver
        /// prewarm, which a state test has no use for.</summary>
        private static HMaestroVirtualController Controller(string profileId)
        {
            var shell = (HMContext)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(HMContext));
            return new HMaestroVirtualController(shell, Profile(profileId), VirtualControllerType.Nintendo);
        }

        /// <summary>A Nintendo slot's raw frame at rest: four stick axes,
        /// one word of buttons, and a centered hat for the profile that has
        /// one.</summary>
        private static RawHidState Rest()
        {
            var raw = RawHidState.Create(4, 32, 1);
            raw.Povs[0] = -1;
            return raw;
        }

        // ── HIDMaestro's side, run as HMController.SubmitState runs it ──

        /// <summary>The six values SubmitState resolves before it encodes:
        /// each stick axis the profile declares, 0.5 when the state has
        /// none, and the two triggers through HMController.ResolveTrigger
        /// on the axes its ResolveCanonicalAxis names.</summary>
        private static double[] Resolved(HMProfile p, HMGamepadState s)
        {
            double Axis(HMAxis a) =>
                s.Axes != null && s.Axes.TryGetValue(a, out float v) ? Math.Clamp(v, 0f, 1f) : 0.5;
            var sticks = p.Sticks;
            Assert.Equal(2, sticks.Count);

            var controller = Sdk.GetType("HIDMaestro.HMController", true);
            var canonical = controller.GetMethod("ResolveCanonicalAxis", Any);
            var trigger = controller.GetMethod("ResolveTrigger", Any);
            Assert.NotNull(canonical);
            Assert.NotNull(trigger);
            var lt = (HMAxis)canonical.Invoke(null, new object[] { p.AxisMap, "lefttrigger", HMAxis.Z });
            var rt = (HMAxis)canonical.Invoke(null, new object[] { p.AxisMap, "righttrigger", HMAxis.Rz });
            return new[]
            {
                Axis(sticks[0].XAxis), Axis(sticks[0].YAxis),
                Axis(sticks[1].XAxis), Axis(sticks[1].YAxis),
                (double)trigger.Invoke(null, new object[] { s.Axes, p.Triggers, 0, lt }),
                (double)trigger.Invoke(null, new object[] { s.Axes, p.Triggers, 1, rt }),
            };
        }

        /// <summary>The plain profile's report 0x09 for a state, from
        /// VendorBlobCodec.EncodeInput.</summary>
        private static byte[] PlainReport(HMGamepadState s)
        {
            var p = Profile(Plain);
            var v = Resolved(p, s);
            var codec = Sdk.GetType("HIDMaestro.Internal.VendorBlobCodec", true);
            object counters = Activator.CreateInstance(codec.GetNestedType("EncoderState", Any));
            var report = new byte[p.ExtendedReport.Size];
            codec.GetMethod("EncodeInput", Any).Invoke(null, new object[]
            {
                p.ExtendedReport, s,
                (float)v[0], (float)v[1], (float)v[2], (float)v[3], (float)v[4], (float)v[5],
                report, counters,
            });
            return report;
        }

        /// <summary>The composite's 24-byte state body for a state, from
        /// Switch2ProPacker.BuildBody: buttons as a u32, four stick values,
        /// six motion counts.</summary>
        private static byte[] FullBody(HMGamepadState s)
        {
            var v = Resolved(Profile(Full), s);
            var packer = Sdk.GetType("HIDMaestro.Internal.Switch2ProPacker", true);
            var body = new byte[(int)packer.GetField("BodySize", Any).GetValue(null)];
            packer.GetMethod("BuildBody", Any).Invoke(null, new object[]
            {
                s, v[0], v[1], v[2], v[3], v[4], v[5], body,
            });
            return body;
        }

        /// <summary>The 21 button bits a profile's own encoder puts out, in
        /// the order of report 0x09's three button bytes.</summary>
        private static uint WireButtons(string profileId, HMGamepadState s)
        {
            if (profileId == Plain)
            {
                var r = PlainReport(s);
                return (uint)(r[3] | (r[4] << 8) | (r[5] << 16));
            }
            var b = FullBody(s);
            return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        }

        private static int U16(byte[] b, int at) => b[at] | (b[at + 1] << 8);
        private static int S16(byte[] b, int at) => (short)U16(b, at);

        // ── The catalog ──

        /// <summary>The Nintendo picker offers the persona beside the two
        /// profiles it had, and the Extended picker does not list it.</summary>
        [Fact]
        public void TheNintendoPickerOffersThePersona()
        {
            var nintendo = HMaestroProfileCatalog.NintendoProfiles.Select(p => p.Id).ToArray();
            Assert.Contains(S1, nintendo);
            Assert.Contains(Plain, nintendo);
            Assert.Contains(Full, nintendo);
            Assert.Equal(3, nintendo.Length);
            Assert.DoesNotContain(HMaestroProfileCatalog.ExtendedProfiles, p => p.Id == Full);
        }

        /// <summary>A new Nintendo slot stays on the first Pro Controller.
        /// The persona reports to Steam and to SDL built with libusb alone,
        /// so it is a choice and never a default.</summary>
        [Fact]
        public void TheDefaultIsNotThePersona()
        {
            Assert.Equal(S1, InputManager.GetDefaultProfileId(VirtualControllerType.Nintendo));
        }

        /// <summary>The persona's report 0x05 carries the accelerometer and
        /// the gyro, so its Motion rows carry no note. The plain profile's
        /// report 0x09 has neither and keeps its note.</summary>
        [Fact]
        public void OnlyThePlainProfilesReportCarriesNoMotion()
        {
            Assert.True(HMaestroProfileCatalog.ReportCarriesNoMotion(Plain));
            Assert.False(HMaestroProfileCatalog.ReportCarriesNoMotion(Full));
            Assert.False(HMaestroProfileCatalog.ReportCarriesNoMotion(S1));
        }

        /// <summary>The persona is the same pad as the plain profile: one
        /// wire table, one set of art, the same 21 lettered rows.</summary>
        [Fact]
        public void ThePersonaSharesThePlainProfilesWire()
        {
            Assert.Equal(NintendoPreviewMap.Family.Switch2Pro, NintendoPreviewMap.FamilyOf(Full));
            Assert.True(NintendoPreviewMap.SameWireFamily(Plain, Full));
            Assert.Same(NintendoPreviewMap.ButtonTable(Plain), NintendoPreviewMap.ButtonTable(Full));
            Assert.Equal(21, PadForge.ViewModels.MacroButtonNames.NintendoLetteredCountFor(Full));
            Assert.Equal(("SWITCH2PRO", "Switch2Pro"),
                HMaestroProfileCatalog.ResolveAssetFolders(Full, VirtualControllerType.Nintendo));

            var plain = Profile(Plain);
            var full = Profile(Full);
            Assert.Equal(plain.StickCount, full.StickCount);
            Assert.Equal(plain.TriggerCount, full.TriggerCount);
            Assert.Equal(plain.HasHat, full.HasHat);
            Assert.Equal(plain.DescriptorHex, full.DescriptorHex);
        }

        // ── Buttons ──

        /// <summary>Every row presses its own control. Row N is bit N of
        /// report 0x09's button bytes by the wire table's own definition, so
        /// a row pressed alone has to set that bit and no other, through the
        /// encoder of the profile the slot runs.</summary>
        [Theory]
        [InlineData(Plain)]
        [InlineData(Full)]
        public void EveryRowReachesItsOwnWireBit(string profileId)
        {
            var vc = Controller(profileId);
            var table = NintendoPreviewMap.ButtonTable(profileId);
            Assert.Equal(21, table.Length);
            for (int row = 0; row < table.Length; row++)
            {
                var raw = Rest();
                raw.SetButton(row, true);
                uint wire = WireButtons(profileId, vc.BuildRawHidState(raw, 2, 0, default));
                Assert.True(wire == 1u << row,
                    $"{profileId}: the {table[row]} row (raw {row}) set wire bits 0x{wire:X6}, not 0x{1u << row:X6}");
            }
        }

        /// <summary>The rows in report 0x09's order, by the names HIDMaestro's
        /// field list gives them. The wire table says the same thing in
        /// PadForge's vocabulary, and this is the check that the two agree:
        /// B is bit 0 and C is bit 20.</summary>
        [Fact]
        public void TheWireTableIsReport09sButtonOrder()
        {
            string[] roles =
            {
                "ButtonB", "ButtonA", "ButtonY", "ButtonX", "RightShoulder", "RightTrigger", "ButtonStart", "RightThumbButton",
                "DPadDown", "DPadRight", "DPadLeft", "DPadUp", "LeftShoulder", "LeftTrigger", "ButtonBack", "LeftThumbButton",
                "ButtonGuide", "ButtonShare", "RightPaddle", "LeftPaddle", "ButtonC",
            };
            string[] names =
            {
                "B", "A", "Y", "X", "RightBumper", "RT_DIGITAL", "Start", "RightStick",
                "DPAD_DOWN", "DPAD_RIGHT", "DPAD_LEFT", "DPAD_UP", "LeftBumper", "LT_DIGITAL", "Back", "LeftStick",
                "Guide", "Share", "RightPaddle", "LeftPaddle", "Misc1",
            };
            Assert.Equal(roles, NintendoPreviewMap.ButtonTable(Plain));

            // HIDMaestro's own list, off the profile on disk.
            var listed = new System.Collections.Generic.List<string>();
            var spec = Profile(Plain).ExtendedReport;
            var fields = (System.Collections.IEnumerable)spec.GetType().GetProperty("Fields", Any).GetValue(spec);
            foreach (object f in fields)
            {
                if ((string)f.GetType().GetProperty("Type", Any).GetValue(f) != "button-mask") continue;
                var buttons = (System.Collections.IEnumerable)f.GetType().GetProperty("Buttons", Any).GetValue(f);
                listed.AddRange(buttons.Cast<object>().Select(o => o.ToString()));
            }
            Assert.Equal(names, listed);
        }

        /// <summary>The D-pad rows reach the hat HIDMaestro reads the D-pad
        /// from. A diagonal sets both of its bits, and a POV the raw frame
        /// happens to carry sets none: this pad's grid has no hat row.</summary>
        [Theory]
        [InlineData(Plain)]
        [InlineData(Full)]
        public void TheDPadRowsComposeThroughTheHat(string profileId)
        {
            var vc = Controller(profileId);
            int up = NintendoPreviewMap.IndexOf(profileId, "DPadUp");
            int right = NintendoPreviewMap.IndexOf(profileId, "DPadRight");

            var raw = Rest();
            raw.SetButton(up, true);
            raw.SetButton(right, true);
            var state = vc.BuildRawHidState(raw, 2, 0, default);
            Assert.Equal(HMHat.NorthEast, state.Hat);
            Assert.Equal((1u << up) | (1u << right), WireButtons(profileId, state));

            var stray = Rest();
            stray.Povs[0] = 9000;
            state = vc.BuildRawHidState(stray, 2, 0, default);
            Assert.Equal(HMHat.None, state.Hat);
            Assert.Equal(0u, WireButtons(profileId, state));
        }

        /// <summary>A slot at rest presses nothing and centers both sticks.
        /// The trigger axes are written released, so ZL and ZR never depend
        /// on what HIDMaestro reads when an axis is missing: under 1.10.1 it
        /// read the right stick's Y, and a resting slot held ZR.</summary>
        [Theory]
        [InlineData(Plain)]
        [InlineData(Full)]
        public void ARestingSlotPressesNothing(string profileId)
        {
            var state = Controller(profileId).BuildRawHidState(Rest(), 2, 0, default);
            var p = Profile(profileId);
            var v = Resolved(p, state);
            Assert.Equal(0.0, v[4]);
            Assert.Equal(0.0, v[5]);
            Assert.Equal(0u, WireButtons(profileId, state));
            Assert.Equal(HMButton.None, state.Buttons);
            Assert.Equal(HMHat.None, state.Hat);

            // Both X axes sit on the center the persona's calibration
            // states. A Y axis may sit one count under it: the raw
            // surface's zero is 32768 of 65535, a hair past half, and the
            // persona turns Y over before it rounds.
            var body = FullBody(state);
            Assert.Equal(0x800, U16(body, 4));
            Assert.Equal(0x800, U16(body, 8));
            Assert.InRange(U16(body, 6), 0x7FF, 0x800);
            Assert.InRange(U16(body, 10), 0x7FF, 0x800);
        }

        /// <summary>The first Pro Controller keeps its raw mask and its hat.
        /// HIDMaestro's SwitchProPacker reads bit N as button N, so a row
        /// renamed for the Switch 2 Pro would press the wrong control
        /// there.</summary>
        [Fact]
        public void TheFirstProKeepsItsRawMask()
        {
            var raw = Rest();
            raw.SetButton(0, true);
            raw.SetButton(5, true);
            raw.SetButton(13, true);
            raw.Povs[0] = 9000;
            var state = Controller(S1).BuildRawHidState(raw, 2, 0, default);
            Assert.Equal((HMButton)((1u << 0) | (1u << 5) | (1u << 13)), state.Buttons);
            Assert.Equal(HMHat.East, state.Hat);
            Assert.Null(Switch2ProStateMap.ForProfile(S1));
            Assert.NotNull(Switch2ProStateMap.ForProfile(Plain));
            Assert.NotNull(Switch2ProStateMap.ForProfile(Full));
        }

        // ── Sticks and motion on the persona ──

        /// <summary>The persona's sticks run 0 at the left and 4095 at the
        /// right, and 4095 at the top, which is how SDL reads report 0x05.
        /// The raw surface is HID-shaped, down positive.</summary>
        [Fact]
        public void TheSticksReachThePersonasBody()
        {
            var raw = Rest();
            raw.Axes[0] = short.MaxValue;   // left stick right
            raw.Axes[1] = short.MinValue;   // left stick up
            raw.Axes[2] = short.MinValue;   // right stick left
            raw.Axes[3] = short.MaxValue;   // right stick down
            var body = FullBody(Controller(Full).BuildRawHidState(raw, 2, 0, default));
            Assert.Equal(4095, U16(body, 4));
            Assert.Equal(4095, U16(body, 6));
            Assert.Equal(0, U16(body, 8));
            Assert.Equal(0, U16(body, 10));
        }

        /// <summary>The slot's motion leaves as the pad's raw counts. The
        /// expected values are HIDMaestro#66's: 1 g is 4096 counts and
        /// 100 degrees a second is 1643, on the wire field SDL reads back as
        /// the axis that was submitted, with its sign.</summary>
        [Fact]
        public void TheSlotsMotionReachesThePersonasBody()
        {
            var motion = new MotionSnapshot
            {
                HasMotion = true,
                AccelX = 0.25f, AccelY = 1f, AccelZ = -0.5f,
                GyroPitch = 100f, GyroYaw = -50f, GyroRoll = 25f,
            };
            var state = Controller(Full).BuildRawHidState(Rest(), 2, 0, motion);
            Assert.Equal(0.25f, state.AccelGX);
            Assert.Equal(1f, state.AccelGY);
            Assert.Equal(-0.5f, state.AccelGZ);
            Assert.Equal(100f, state.GyroDpsX);
            Assert.Equal(-50f, state.GyroDpsY);
            Assert.Equal(25f, state.GyroDpsZ);

            var body = FullBody(state);
            Assert.Equal(1024, S16(body, 12));    // accelerometer X
            Assert.Equal(2048, S16(body, 14));    // minus Z
            Assert.Equal(4096, S16(body, 16));    // Y: 1 g
            Assert.Equal(1643, S16(body, 18));    // gyro X: 100 degrees a second
            Assert.Equal(-411, S16(body, 20));    // minus Z
            Assert.Equal(-822, S16(body, 22));    // Y

            // A slot with no motion mapped sends none.
            body = FullBody(Controller(Full).BuildRawHidState(Rest(), 2, 0, default));
            for (int at = 12; at < body.Length; at++) Assert.Equal(0, body[at]);
        }

        // ── No finished report ──

        /// <summary>The persona takes state and no finished report. Its
        /// device side picks the report and stamps the counters, and
        /// HMController.SubmitRawReport and SubmitRawExtendedReport throw
        /// NotSupportedException on it, so no packer may name it and its
        /// layout must fit the state.</summary>
        [Fact]
        public void APersonaThatBuildsItsOwnReportsHasNoPacker()
        {
            var own = HMaestroProfileCatalog.AllProfiles
                .Where(HMaestroVirtualController.DeviceBuildsReports)
                .Select(p => p.Id).ToArray();
            Assert.Equal(new[] { Full }, own);

            Assert.False(HMaestroVirtualController.DeviceBuildsReports(Profile(Plain)));
            Assert.False(HMaestroVirtualController.DeviceBuildsReports(Profile("dualsense-composite")));
            Assert.False(HMaestroVirtualController.DeviceBuildsReports(Profile("steam-deck-composite")));
            Assert.False(HMaestroVirtualController.DeviceBuildsReports(null));

            foreach (string id in own)
            {
                Assert.Null(ValveReportPackers.ForProfile(id));
                Assert.Null(SonyReportPackers.ForProfile(id));
            }

            var p = Profile(Full);
            var layout = new CustomControllerLayout
            {
                Sticks = p.StickCount,
                Triggers = p.TriggerCount,
                Povs = p.HasHat ? 1 : 0,
                Buttons = NintendoPreviewMap.ButtonCount(Full),
            };
            Assert.False(ExtendedReportPacker.NeedsRawReport(layout));

            Assert.True(Controller(Full).BuildsItsOwnReports);
            Assert.False(Controller(Plain).BuildsItsOwnReports);
        }

        /// <summary>Step 5 never hands such a persona a packed report,
        /// whatever its layout counts say. The call throws, and it would
        /// throw on every poll.</summary>
        [Fact]
        public void TheSubmitPathChecksBeforeItPacksAReport()
        {
            string step5 = AuditDelta20261002Tests.RepoText(
                "PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs");
            int calls = System.Text.RegularExpressions.Regex.Matches(
                step5, @"ExtendedReportPacker\.NeedsRawReport\(").Count;
            int guarded = System.Text.RegularExpressions.Regex.Matches(
                step5, @"!hmExt\.BuildsItsOwnReports\s*&&\s*ExtendedReportPacker\.NeedsRawReport\(").Count;
            Assert.Equal(1, calls);
            Assert.Equal(calls, guarded);
        }

        // ── Rumble ──

        private delegate void DecodeRumble(ReadOnlySpan<byte> data, out byte leftMotor, out byte rightMotor);

        /// <summary>The bundled SDK decodes the persona's output report 0x02
        /// into the two motor bytes the Nintendo arm of OutputDecoded reads.
        /// The frame is the one SDL writes for SDL_RumbleGamepad(0x8000,
        /// 0x4000), in both actuator blocks. HIDMaestro 1.10.1 has no such
        /// decode.</summary>
        [Fact]
        public void TheBundledSdkDecodesThePersonasRumble()
        {
            var packer = Sdk.GetType("HIDMaestro.Internal.Switch2ProPacker", true);
            var decode = (DecodeRumble)packer.GetMethod("DecodeRumble", Any)
                .CreateDelegate(typeof(DecodeRumble));

            var data = new byte[32];
            byte[] frame = { 0x87, 0xC5, 0x21, 0x91, 0x38 };
            data[0] = 0x50;
            data[16] = 0x50;
            frame.CopyTo(data, 1);
            frame.CopyTo(data, 17);
            decode(data, out byte left, out byte right);
            Assert.Equal(127, left);
            Assert.Equal(64, right);

            decode(new byte[32], out left, out right);
            Assert.Equal(0, left);
            Assert.Equal(0, right);

            // The arm that takes the pair trusts a Nintendo profile's motors
            // without a Sony validity flag.
            Assert.Equal(0x057E, Profile(Full).VendorId);
            Assert.True(HMaestroVirtualController.MotorWriteAllowed(Profile(Full).VendorId, false));
        }

        // ── Self-readback ──

        /// <summary>PadForge must not open its own persona. The fork's
        /// enumeration filter drops it, and Step 1's second check walks to
        /// the emulated host controller for the vendors that have a
        /// persona an SDL driver would fight. Any other vendor skips the
        /// walk, so a real pad's arrival costs no device-tree read.</summary>
        [Fact]
        public void TheAncestryCheckCoversNintendo()
        {
            Assert.True(InputManager.ChecksUsbipAncestry(0x054C));
            Assert.True(InputManager.ChecksUsbipAncestry(0x057E));
            Assert.False(InputManager.ChecksUsbipAncestry(0x045E));
            Assert.False(InputManager.ChecksUsbipAncestry(0x0000));
        }
    }
}
