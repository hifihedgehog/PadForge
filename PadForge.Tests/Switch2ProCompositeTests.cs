using System;
using System.Collections.Generic;
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
    /// disk: VendorBlobCodec for the plain profile's report 0x09 and for the
    /// composite's 24-byte state block, which HIDMaestro 2.0.0 made profile
    /// data in place of Switch2ProPacker. That is the
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

        /// <summary>The composite's 24-byte state block for a state: buttons
        /// as a u32, four stick values, six motion counts. HIDMaestro 2.0.0
        /// made the persona profile data, and SubmitState encodes the
        /// profile's stateBlock fields with VendorBlobCodec.EncodeInput over
        /// StateBlockSpecForCodec, or runs the state program the build
        /// generated from those same fields (HMController.SubmitState).</summary>
        private static byte[] FullBody(HMGamepadState s)
        {
            var p = Profile(Full);
            var v = Resolved(p, s);
            object inner = typeof(HMProfile).GetProperty("Inner", Any).GetValue(p);
            object spec = inner.GetType().GetMethod("StateBlockSpecForCodec", Any).Invoke(inner, null);
            var codec = Sdk.GetType("HIDMaestro.Internal.VendorBlobCodec", true);
            object counters = Activator.CreateInstance(codec.GetNestedType("EncoderState", Any));
            var body = new byte[(int)spec.GetType().GetProperty("Size", Any).GetValue(spec)];
            codec.GetMethod("EncodeInput", Any).Invoke(null, new object[]
            {
                spec, s,
                (float)v[0], (float)v[1], (float)v[2], (float)v[3], (float)v[4], (float)v[5],
                body, counters,
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

        /// <summary>The Nintendo picker offers the persona beside the
        /// profiles it had, the first Pro Controller on Bluetooth and on USB
        /// (switch-pro-usb, HIDMaestro 2.0.0) among them, and the Extended
        /// picker does not list it.</summary>
        [Fact]
        public void TheNintendoPickerOffersThePersona()
        {
            var nintendo = HMaestroProfileCatalog.NintendoProfiles.Select(p => p.Id).ToArray();
            Assert.Contains(S1, nintendo);
            Assert.Contains("switch-pro-usb", nintendo);
            Assert.Contains(Plain, nintendo);
            Assert.Contains(Full, nintendo);
            Assert.Equal(4, nintendo.Length);
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

        /// <summary>A persona that builds its own reports takes state and no
        /// finished report. Its device side picks the report and stamps the
        /// counters, and a raw submit on it must be its state block
        /// (HMController.SubmitRawReport throws ArgumentException for another
        /// length), so no packer may name it and the composite's layout must
        /// fit the state. The set is HIDMaestro's own
        /// (HMProfile.BuildsItsOwnReports): from 2.0.0 the Switch Pro on both
        /// descriptors and the DualShock 3 build theirs as the Switch 2 Pro
        /// composite does. From 2.3.0 so do the Joy-Cons and the Nintendo
        /// Switch Online pads, each on its Bluetooth descriptor and in its
        /// USB form, which now answer the Switch protocol, and
        /// xbox-one-s-full, the wired Xbox One S pad Windows' own Xbox
        /// drivers bind.</summary>
        [Fact]
        public void APersonaThatBuildsItsOwnReportsHasNoPacker()
        {
            var own = HMaestroProfileCatalog.AllProfiles
                .Where(HMaestroVirtualController.DeviceBuildsReports)
                .Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                "dualshock-3-full",
                "genesis-nso", "genesis-nso-usb", "joycon-l", "joycon-l-usb", "joycon-r", "joycon-r-usb",
                "n64-nso", "n64-nso-usb", "snes-nso", "snes-nso-usb",
                S1, "switch-pro-usb", Full,
                "xbox-one-s-full",
            }, own);

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

        /// <summary>An output report's data, after its ID, through the
        /// profile's own protocol decoder, as HMController.RunDecoder runs it
        /// before it raises OutputDecoded: the compiled device model's
        /// decoders, bound by ProgramBinder, the one that names the report
        /// ID, its output read back by its semantic names.</summary>
        private static Dictionary<string, byte> DecodeOutput(string profileId, byte reportId, byte[] data)
        {
            object inner = typeof(HMProfile).GetProperty("Inner", Any).GetValue(Profile(profileId));
            object model = inner.GetType().GetMethod("GetOrCompileModel", Any).Invoke(inner, null);
            var binder = Sdk.GetType("HIDMaestro.Internal.ProgramBinder", true);
            var form = Sdk.GetType("HIDMaestro.Internal.Programs.ProgramForm", true);
            int slack = (int)form.GetField("Slack", Any).GetValue(null);
            int maxReport = (int)form.GetField("MaxReport", Any).GetValue(null);
            foreach (object runner in (Array)binder.GetMethod("BindDecoders", Any).Invoke(null, new object[] { model, false }))
            {
                object decoder = runner.GetType().GetProperty("Form", Any).GetValue(runner);
                if (Array.IndexOf((byte[])decoder.GetType().GetProperty("Reports").GetValue(decoder), reportId) < 0) continue;
                int min = (int)decoder.GetType().GetProperty("MinLength").GetValue(decoder);
                var scratch = new byte[Math.Max(min, data.Length + 1) + slack + 1];
                var output = GC.AllocateArray<byte>(maxReport, pinned: true);
                bool decoded = (bool)runner.GetType().GetMethod("RunOutput", Any).Invoke(runner, new object[]
                {
                    reportId, data, data.Length, scratch,
                    System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(output, 0),
                });
                Assert.True(decoded, $"report 0x{reportId:X2} of {data.Length} bytes was not decoded");
                var semantics = (string[])decoder.GetType().GetProperty("Semantics").GetValue(decoder);
                var fields = new Dictionary<string, byte>();
                for (int i = 0; i < semantics.Length; i++) fields[semantics[i]] = output[i];
                return fields;
            }
            throw new Xunit.Sdk.XunitException($"{profileId} decodes no output report 0x{reportId:X2}");
        }

        /// <summary>The bundled SDK decodes the persona's output report 0x02
        /// into the two motor bytes the Nintendo arm of OutputDecoded reads,
        /// leftMotor and rightMotor. The frame is the one SDL writes for
        /// SDL_RumbleGamepad(0x8000, 0x4000), in both actuator blocks.
        /// HIDMaestro 1.10.1 has no such decode, and 2.0.0 moved it from
        /// code into the profile's protocol.decoders.rumble.</summary>
        [Fact]
        public void TheBundledSdkDecodesThePersonasRumble()
        {
            var data = new byte[32];
            byte[] frame = { 0x87, 0xC5, 0x21, 0x91, 0x38 };
            data[0] = 0x50;
            data[16] = 0x50;
            frame.CopyTo(data, 1);
            frame.CopyTo(data, 17);
            var motors = DecodeOutput(Full, 0x02, data);
            Assert.Equal(127, motors["leftMotor"]);
            Assert.Equal(64, motors["rightMotor"]);

            motors = DecodeOutput(Full, 0x02, new byte[32]);
            Assert.Equal(0, motors["leftMotor"]);
            Assert.Equal(0, motors["rightMotor"]);

            // The arm that takes the pair trusts a Nintendo profile's motors
            // without a Sony validity flag.
            Assert.Equal(0x057E, Profile(Full).VendorId);
            Assert.True(HMaestroVirtualController.MotorWriteAllowed(Profile(Full).VendorId, false));
        }

        // ── Self-readback ──

        /// <summary>PadForge must not open its own persona. The fork's
        /// enumeration filter drops it under a host controller HIDMaestro
        /// has stamped, and Step 1's second check takes a device under the
        /// emulated host controller for PadForge's own when it has the ids
        /// of a persona this process makes, from before HIDMaestro creates
        /// it until a grace after it is disposed. A real Switch Pro, Switch
        /// 2 Pro or DualSense forwarded over USB/IP, or another program's
        /// virtual pad there, is no persona of PadForge's when none of its
        /// model is live, so the check lets it through. It reaches the
        /// check only under a host controller with no stamp: under a
        /// stamped one the forks drop it with the personas. The check once
        /// held for every Sony and Nintendo device and passed those over as
        /// PadForge's own.</summary>
        [Fact]
        public void TheAncestryCheckCoversOnlyAPersonaThisProcessMakes()
        {
            var owner = new object();
            long now = Environment.TickCount64;
            try
            {
                Assert.False(InputManager.ChecksUsbipAncestry(0x057E, 0x2009, now));   // Switch Pro
                Assert.False(InputManager.ChecksUsbipAncestry(0x057E, 0x2069, now));   // Switch 2 Pro
                Assert.False(InputManager.ChecksUsbipAncestry(0x054C, 0x0CE6, now));   // DualSense

                InputManager.NotePersonaLive(owner, Profile(Full).VendorId, Profile(Full).ProductId);
                Assert.True(InputManager.ChecksUsbipAncestry(0x057E, 0x2069, now));
                Assert.False(InputManager.ChecksUsbipAncestry(0x057E, 0x2009, now));
                Assert.False(InputManager.ChecksUsbipAncestry(0x054C, 0x0CE6, now));
                Assert.False(InputManager.ChecksUsbipAncestry(0x045E, 0x0B12, now));

                // A disposed persona still counts while its device can linger
                // in the tree, and not after.
                InputManager.NotePersonaRetired(owner, now);
                Assert.True(InputManager.ChecksUsbipAncestry(0x057E, 0x2069, now + 1));
                Assert.False(InputManager.ChecksUsbipAncestry(0x057E, 0x2069,
                    now + InputManager.PersonaRetireGraceMs));
            }
            finally
            {
                // Retire it (a no-op once retired) and let the grace run out,
                // so the entry does not outlive the test.
                InputManager.NotePersonaRetired(owner, now);
                InputManager.ChecksUsbipAncestry(0, 0, now + InputManager.PersonaRetireGraceMs);
            }
        }

        /// <summary>A persona can outlive the session that made it, until the
        /// next launch's sweep detaches it. While that sweep runs the walk
        /// covers every composite profile's ids, the Switch 2 Pro's and the
        /// DualSense's among them, and still skips a model no composite
        /// carries, such as the first Switch Pro Controller.</summary>
        [Fact]
        public void TheAncestryCheckCoversEveryCompositeModelWhileTheLaunchSweepRuns()
        {
            var saved = InputManager.LaunchSweepPending;
            long now = Environment.TickCount64;
            try
            {
                Assert.False(InputManager.ChecksUsbipAncestry(0x057E, 0x2069, now));   // the sweep done
                InputManager.LaunchSweepPending = () => true;
                Assert.True(InputManager.ChecksUsbipAncestry(0x057E, 0x2069, now));
                Assert.True(InputManager.ChecksUsbipAncestry(0x054C, 0x0CE6, now));
                Assert.False(InputManager.ChecksUsbipAncestry(0x057E, 0x2009, now));
                Assert.False(InputManager.ChecksUsbipAncestry(0x045E, 0x0B12, now));
            }
            finally { InputManager.LaunchSweepPending = saved; }
        }

        /// <summary>A real pad attached over USB/IP while a persona of its
        /// model is live, or while the launch sweep runs, reads as PadForge's
        /// own and is passed over. Step 1 kept it out for as long as it stayed
        /// attached. It is opened again once no persona of its ids is in
        /// scope. A device rejected on a marker of its own stays out, and an
        /// id SDL no longer lists is forgotten.</summary>
        [Fact]
        public void ADevicePassedOverOnAncestryAloneIsProbedAgainOnceItsScopeLapses()
        {
            var owner = new object();
            var saved = InputManager.LaunchSweepPending;
            long now = Environment.TickCount64;
            var held = new InputManager.SelfVirtualSuppressions();
            var attached = new HashSet<uint> { 7, 8, 9 };
            try
            {
                InputManager.LaunchSweepPending = () => true;
                InputManager.NotePersonaLive(owner, Profile(Full).VendorId, Profile(Full).ProductId);
                held.Add(7, 0x057E, 0x2069, byAncestryOnly: true);    // a Switch 2 Pro, on the walk alone
                held.Add(8, 0x057E, 0x2069, byAncestryOnly: false);   // one with an HM-CTL- serial
                held.Add(9, 0x054C, 0x0CE6, byAncestryOnly: true);    // a DualSense seen during the launch sweep
                Assert.Null(held.ReleaseLapsed(attached, now));
                Assert.True(held.Contains(7) && held.Contains(8) && held.Contains(9));

                // The sweep done: no DualSense persona is live, so that one is read.
                InputManager.LaunchSweepPending = () => false;
                Assert.Equal(new uint[] { 9 }, held.ReleaseLapsed(attached, now));
                Assert.False(held.Contains(9));
                Assert.True(held.Contains(7) && held.Contains(8));

                // The Switch 2 Pro persona disposed: held through the grace, read after it.
                InputManager.NotePersonaRetired(owner, now);
                Assert.Null(held.ReleaseLapsed(attached, now + 1));
                Assert.True(held.Contains(7));
                Assert.Equal(new uint[] { 7 }, held.ReleaseLapsed(attached, now + InputManager.PersonaRetireGraceMs));
                Assert.False(held.Contains(7));
                Assert.True(held.Contains(8));
                Assert.Null(held.ReleaseLapsed(attached, now + InputManager.PersonaRetireGraceMs));

                // PadForge's own persona leaves the tree with its scope still
                // open: forgotten, with nothing to open.
                InputManager.LaunchSweepPending = () => true;
                held.Add(10, 0x054C, 0x0CE6, byAncestryOnly: true);
                Assert.Null(held.ReleaseLapsed(attached, now));
                Assert.False(held.Contains(10));
            }
            finally
            {
                InputManager.LaunchSweepPending = saved;
                InputManager.NotePersonaRetired(owner, now);
                InputManager.ChecksUsbipAncestry(0, 0, now + InputManager.PersonaRetireGraceMs);
            }
        }

        // ── The calls that put a persona in scope ──
        //
        // The scope is fed by HMaestroVirtualController.Connect and
        // Disconnect and read by Step 1. HIDMaestro's own call is replaced
        // (CreateControllerSeam), since it would create a device, and the
        // device-tree reads too (UsbipAncestryOf, InDeviceTree), since no
        // test has a persona's device node. Everything between runs as the
        // app runs it.

        /// <summary>A time past every grace. A live persona is in scope
        /// there, since no grace runs for one, and a retired one is not.
        /// The check at that time drops every retired persona, so a test
        /// makes it last.</summary>
        private const long Far = long.MaxValue / 2;

        /// <summary>A controller whose HIDMaestro call makes no device and
        /// hands back no SDK controller.</summary>
        private static HMaestroVirtualController Deviceless(string profileId)
        {
            var vc = Controller(profileId);
            vc.CreateControllerSeam = (profile, identityKey) => null;
            return vc;
        }

        /// <summary>Leaves no persona of <paramref name="vc"/> behind for
        /// the next test.</summary>
        private static void Forget(HMaestroVirtualController vc)
        {
            vc.Disconnect();
            InputManager.NotePersonaRetired(vc);
            InputManager.ChecksUsbipAncestry(0, 0, Far);
        }

        /// <summary>Connect records the persona before HIDMaestro makes its
        /// device, so Step 1 knows it at its first enumeration, and
        /// Disconnect retires it, so its ids fall out of scope a grace
        /// later. With either call gone the guard read a persona as a
        /// stranger's device, or kept passing over a real pad of its model
        /// forwarded over USB/IP for the life of the process.</summary>
        [Fact]
        public void ConnectPutsThePersonaInScopeAndDisconnectRetiresIt()
        {
            var profile = Profile(Full);
            var vc = Controller(Full);
            bool? inScopeAtCreation = null;
            vc.CreateControllerSeam = (p, identityKey) =>
            {
                inScopeAtCreation = InputManager.ChecksUsbipAncestry(profile.VendorId, profile.ProductId);
                return null;
            };
            long before = Environment.TickCount64;
            try
            {
                Assert.False(InputManager.ChecksUsbipAncestry(profile.VendorId, profile.ProductId, before));

                vc.Connect();
                Assert.True(inScopeAtCreation, "the persona was not in scope while HIDMaestro made its device");
                Assert.True(vc.IsConnected);
                Assert.True(InputManager.ChecksUsbipAncestry(profile.VendorId, profile.ProductId, Far));
                Assert.False(InputManager.ChecksUsbipAncestry(0x057E, 0x2009, Far));   // its own ids alone

                vc.Disconnect();
                Assert.False(vc.IsConnected);
                // Retired, not dropped: in scope while its device can linger
                // in the tree, and out of it after.
                Assert.True(InputManager.ChecksUsbipAncestry(profile.VendorId, profile.ProductId, before));
                Assert.False(InputManager.ChecksUsbipAncestry(profile.VendorId, profile.ProductId, Far));

                // The same controller connected again is live again.
                vc.Connect();
                vc.Disconnect();
                vc.Connect();
                Assert.True(InputManager.ChecksUsbipAncestry(profile.VendorId, profile.ProductId, Far));
            }
            finally { Forget(vc); }
        }

        /// <summary>A persona HIDMaestro fails to make is retired on the
        /// way out. Left live, its ids stayed in scope for the life of the
        /// process, and a real pad of its model over USB/IP was never
        /// read. No controller was made, so none is disposed.</summary>
        [Fact]
        public void APersonaThatFailsToConnectIsRetired()
        {
            var profile = Profile(Full);
            var vc = Controller(Full);
            int disposed = 0;
            vc.CreateControllerSeam = (p, identityKey) => throw new InvalidOperationException("no device");
            vc.DisposeControllerSeam = controller => disposed++;
            long before = Environment.TickCount64;
            try
            {
                Assert.Throws<InvalidOperationException>(() => vc.Connect());
                Assert.False(vc.IsConnected);
                Assert.Equal(0, disposed);
                Assert.True(InputManager.ChecksUsbipAncestry(profile.VendorId, profile.ProductId, before));
                Assert.False(InputManager.ChecksUsbipAncestry(profile.VendorId, profile.ProductId, Far));
            }
            finally { Forget(vc); }
        }

        /// <summary>A Connect that fails once HIDMaestro has made the
        /// controller takes that controller down on its way out and retires
        /// the persona. The caller's Dispose could not: it calls Disconnect,
        /// which returns at once for a controller that never connected, so
        /// the SDK controller was never disposed and its device stayed, the
        /// persona's ids in scope with it for the life of the process.</summary>
        [Theory]
        [InlineData(Full)]
        [InlineData(Plain)]
        public void AConnectThatFailsOnceTheControllerIsMadeDisposesIt(string profileId)
        {
            var profile = Profile(profileId);
            var vc = Controller(profileId);
            int made = 0, disposed = 0;
            vc.CreateControllerSeam = (p, identityKey) => { made++; return null; };
            vc.DisposeControllerSeam = controller => disposed++;
            vc.ControllerMadeSeam = () => throw new InvalidOperationException("after the device exists");
            try
            {
                Assert.Throws<InvalidOperationException>(() => vc.Connect());
                Assert.Equal((1, 1), (made, disposed));
                Assert.False(vc.IsConnected);
                Assert.False(InputManager.ChecksUsbipAncestry(profile.VendorId, profile.ProductId, Far));

                // Nothing is left for the caller's Dispose to take down.
                vc.Dispose();
                Assert.Equal(1, disposed);
            }
            finally { Forget(vc); }
        }

        /// <summary>Control: a Connect that succeeds disposes nothing on its
        /// way in, and the seam that fails it is asked once per Connect.</summary>
        [Fact]
        public void AConnectThatSucceedsDisposesNothing()
        {
            var vc = Deviceless(Full);
            int asked = 0, disposed = 0;
            vc.ControllerMadeSeam = () => asked++;
            vc.DisposeControllerSeam = controller => disposed++;
            try
            {
                vc.Connect();
                Assert.True(vc.IsConnected);
                Assert.Equal((1, 0), (asked, disposed));
            }
            finally { Forget(vc); }
        }

        /// <summary>Only a composite persona goes in scope. The plain
        /// profile has the persona's ids, but HIDMaestro makes its device on
        /// its own driver, away from the USB/IP host controller the walk
        /// looks for, so its Connect records nothing.</summary>
        [Fact]
        public void AProfileThatIsNoPersonaPutsNothingInScope()
        {
            var plain = Profile(Plain);
            Assert.Equal((Profile(Full).VendorId, Profile(Full).ProductId), (plain.VendorId, plain.ProductId));
            var vc = Deviceless(Plain);
            try
            {
                vc.Connect();
                Assert.True(vc.IsConnected);
                Assert.False(InputManager.ChecksUsbipAncestry(plain.VendorId, plain.ProductId, Far));
            }
            finally { Forget(vc); }
        }

        /// <summary>The launch sweep's window is read off the sweep itself
        /// (App.OrphanSweepTask): open while it runs, shut once it ends and
        /// when the app started none. The tests above replace that read, so
        /// one that always answered no left them green and a persona from a
        /// session that ended without its exit sweep unguarded.</summary>
        [Fact]
        public void TheLaunchSweepWindowIsReadOffTheSweepItself()
        {
            var sweepTask = typeof(App).GetProperty("OrphanSweepTask", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(sweepTask);
            object saved = sweepTask.GetValue(null);
            var sweep = new System.Threading.Tasks.TaskCompletionSource();
            try
            {
                sweepTask.SetValue(null, null);
                Assert.False(InputManager.LaunchSweepPending());
                Assert.False(InputManager.ChecksUsbipAncestry(0x054C, 0x0CE6, Far));

                sweepTask.SetValue(null, sweep.Task);
                Assert.True(InputManager.LaunchSweepPending());
                Assert.True(InputManager.ChecksUsbipAncestry(0x054C, 0x0CE6, Far));    // a DualSense persona left behind
                Assert.False(InputManager.ChecksUsbipAncestry(0x057E, 0x2009, Far));   // no composite has these ids

                sweep.SetResult();
                Assert.False(InputManager.LaunchSweepPending());
                Assert.False(InputManager.ChecksUsbipAncestry(0x054C, 0x0CE6, Far));
            }
            finally
            {
                sweep.TrySetResult();
                sweepTask.SetValue(null, saved);
            }
        }

        /// <summary>Step 1's test of a device it opens, with the scope fed
        /// by a controller's own Connect and Disconnect. A device under the
        /// emulated host controller with a connected persona's ids is
        /// PadForge's own and is passed over. The same model on a real
        /// port, another model over USB/IP, and that model with no persona
        /// connected are let through. Once the persona is disconnected and
        /// its grace has run, a device still listed is opened again and let
        /// through. (Step 1 is handed a device over USB/IP only under a host
        /// controller HIDMaestro has not stamped.)</summary>
        [Fact]
        public void Step1PassesOverAConnectedPersonaAndReadsItsModelOnceItIsGone()
        {
            var profile = Profile(Full);
            ushort vid = profile.VendorId, pid = profile.ProductId;
            const string underVhci = @"\\?\hid#vid_057e&pid_2069&mi_03#under-the-emulated-host#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            const string onAPort = @"\\?\hid#vid_057e&pid_2069&mi_03#on-a-real-port#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var vc = Deviceless(Full);
            var savedWalk = InputManager.UsbipAncestryOf;
            var savedTree = InputManager.InDeviceTree;
            var held = new InputManager.SelfVirtualSuppressions();
            var askedOfTheTree = new List<string>();
            long before = Environment.TickCount64;
            try
            {
                InputManager.UsbipAncestryOf = path => path == underVhci
                    ? UsbipAncestry.UnderVhci : UsbipAncestry.None;
                // The pad stays in the device tree throughout.
                InputManager.InDeviceTree = path => { askedOfTheTree.Add(path); return true; };

                // No persona connected: a Switch 2 Pro over USB/IP is let through.
                Assert.False(held.PassOver(1, "", underVhci, vid, pid));
                Assert.False(held.Contains(1));

                vc.Connect();
                Assert.True(held.PassOver(2, "", underVhci, vid, pid));
                Assert.True(held.Contains(2));
                Assert.False(held.PassOver(3, "", onAPort, vid, pid));
                Assert.False(held.PassOver(4, "", underVhci, 0x057E, 0x2009));
                Assert.False(held.Contains(3) || held.Contains(4));

                // A marker settles it whatever the ids and the tree say, and
                // for the life of the process.
                Assert.True(held.PassOver(5, "HM-CTL-3", onAPort, 0x045E, 0x028E));
                Assert.True(held.PassOver(6, null, @"\\?\hid#hidmaestro&col01#1", 0x045E, 0x028E));
                Assert.True(held.PassOver(7, "HM-CTL-4", underVhci, vid, pid));

                // Step 1 asks before each enumeration. Live, the persona
                // holds its id whatever the clock says.
                var listed = new HashSet<uint> { 2, 5, 6, 7 };
                Assert.Null(held.ReleaseLapsed(listed, Far));

                vc.Disconnect();
                Assert.Null(held.ReleaseLapsed(listed, before));
                Assert.True(held.Contains(2));
                // In scope, the tree is not asked. Out of it, it is asked
                // about the device the id was recorded with.
                Assert.Empty(askedOfTheTree);
                Assert.Equal(new uint[] { 2 }, held.ReleaseLapsed(listed, Far));
                Assert.Equal(new[] { underVhci }, askedOfTheTree);
                Assert.False(held.Contains(2));
                Assert.True(held.Contains(5) && held.Contains(6) && held.Contains(7));

                // Opened again, it is let through: nothing of its ids is in scope.
                Assert.False(held.PassOver(2, "", underVhci, vid, pid));
            }
            finally
            {
                InputManager.UsbipAncestryOf = savedWalk;
                InputManager.InDeviceTree = savedTree;
                Forget(vc);
            }
        }

        /// <summary>A composite persona serves HIDMaestro's own USB serial,
        /// "HM" and twelve hex digits, unless its profile carries a captured
        /// one, so Windows names its USB device by it. A device under the
        /// emulated host controller, below such a USB device, is a persona
        /// whatever the clock says: one whose device outlived its grace was
        /// opened as a controller of the user's, and so was one a session
        /// left behind once the launch sweep had ended. It is passed over
        /// with no persona of its ids in scope, and it stays out for as long
        /// as SDL lists it. A real pad over USB/IP has no such serial and is
        /// let through, the control.</summary>
        [Fact]
        public void ADeviceBelowAPersonasOwnSerialIsPassedOverWhateverTheScope()
        {
            var profile = Profile(Full);
            ushort vid = profile.VendorId, pid = profile.ProductId;
            const string persona = @"\\?\hid#vid_057e&pid_2069&mi_03#a-persona#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            const string forwarded = @"\\?\hid#vid_057e&pid_2069&mi_03#a-forwarded-pad#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var savedWalk = InputManager.UsbipAncestryOf;
            var savedTree = InputManager.InDeviceTree;
            var held = new InputManager.SelfVirtualSuppressions();
            try
            {
                InputManager.UsbipAncestryOf = path => path == persona ? UsbipAncestry.Persona
                    : path == forwarded ? UsbipAncestry.UnderVhci
                    : UsbipAncestry.None;
                InputManager.InDeviceTree = path => true;

                Assert.True(held.PassOver(1, "", persona, vid, pid));
                Assert.False(held.PassOver(2, "", forwarded, vid, pid));
                Assert.True(held.Contains(1));
                Assert.False(held.Contains(2));

                // Held like a marker: no clock releases it, though its
                // device is still in the tree.
                var listed = new HashSet<uint> { 1, 2 };
                Assert.Null(held.ReleaseLapsed(listed, Far));
                Assert.True(held.Contains(1));
            }
            finally
            {
                InputManager.UsbipAncestryOf = savedWalk;
                InputManager.InDeviceTree = savedTree;
            }
        }

        /// <summary>The instance id of a persona's USB device, as Windows
        /// builds it from the serial the device serves. HIDMaestro's is "HM"
        /// and twelve hex digits (DeviceIdentity.SyntheticSerial). A real
        /// pad's id ends in a serial of its own or in a number Windows makes
        /// from its port, and the nodes around a persona's USB device carry
        /// neither.</summary>
        [Theory]
        [InlineData(@"USB\VID_054C&PID_0CE6\HM0123456789AB", true)]
        [InlineData(@"USB\VID_057E&PID_2069\HMFFFFFFFFFFFF", true)]
        [InlineData(@"usb\vid_057e&pid_2069\hm0a1b2c3d4e5f", true)]
        [InlineData(@"USB\VID_054C&PID_0CE6\7&2A8B7C1D&0&2", false)]            // a real DualSense, named by its port
        [InlineData(@"USB\VID_054C&PID_0CE6&MI_03\7&2A8B7C1D&0&0003", false)]   // a persona's function node
        [InlineData(@"HID\VID_054C&PID_0CE6&MI_03\8&1F2E3D4C&0&0000", false)]   // its HID node
        [InlineData(@"USB\VID_28DE&PID_1205\FX1A00000002", false)]              // a captured serial, the Steam Deck's
        [InlineData(@"USB\VID_054C&PID_0CE6\HM0123456789A", false)]             // eleven digits
        [InlineData(@"USB\VID_054C&PID_0CE6\HM0123456789ABC", false)]           // thirteen
        [InlineData(@"USB\VID_054C&PID_0CE6\HM01234567G9AB", false)]            // no hex digit
        [InlineData(@"USB\VID_054C&PID_0CE6\XM0123456789AB", false)]
        [InlineData(@"SWD\HIDMAESTRO\HM0123456789AB", false)]                   // no USB device
        [InlineData(@"USB\ROOT_HUB30\5&1A2B3C4D&0&0", false)]
        [InlineData(@"ROOT\USB\0000", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void APersonasUsbDeviceIsNamedByHidMaestrosSerial(string instanceId, bool persona)
            => Assert.Equal(persona, HidMaestroAncestry.IsPersonaUsbInstanceId(instanceId));

        // ── The walk's rule, on the hops of a device tree ──
        //
        // Each tree is written as the walk reads it, the device's own node
        // first: instance id, service, hardware ids. A persona's is the tree
        // measured on hardware (the guard's header). A controller on
        // HIDMaestro's own driver hangs off a node that carries the hardware
        // ids HIDMaestro's source writes (DeviceNodeCreator.CreateDeviceNode,
        // DeviceOrchestrator.CreateGamepadCompanion).

        private static AncestryHop Hop(string instanceId, string service, params string[] hardwareIds)
            => new(instanceId, hardwareIds, service);

        /// <summary>A HID interface four nodes below the usbip-win2 emulated
        /// host controller: a composite persona's, or that of a pad attached
        /// over usbip-win2. <paramref name="usbDevice"/> ends its USB
        /// device's instance id, HIDMaestro's serial for a persona that
        /// serves it.</summary>
        private static AncestryHop[] OverUsbip(string vidPid, string usbDevice, bool stamped, bool serviceReads) => new[]
        {
            Hop($@"HID\{vidPid}&MI_03\8&1F2E3D4C&0&0000", null, $@"HID\{vidPid}&MI_03"),
            Hop($@"USB\{vidPid}&MI_03\7&2A8B7C1D&0&0003", "HidUsb", $@"USB\{vidPid}&MI_03"),
            Hop($@"USB\{vidPid}\{usbDevice}", "usbccgp", $@"USB\{vidPid}"),
            Hop(@"USB\ROOT_HUB30\5&1A2B3C4D&0&0", "USBHUB3", @"USB\ROOT_HUB30"),
            Hop(@"ROOT\USB\0000", serviceReads ? "usbip2_ude" : null,
                stamped ? new[] { @"ROOT\USBIP_WIN2\UDE", @"ROOT\HIDMAESTRO_UDE" } : new[] { @"ROOT\USBIP_WIN2\UDE" }),
            Hop(@"HTREE\ROOT\0", null),
        };

        /// <summary>The HID interface of a virtual controller on HIDMaestro's
        /// own driver, below the node HIDMaestro creates for it.</summary>
        private static AncestryHop[] OnHidMaestrosDriver(string vidPid) => new[]
        {
            Hop($@"HID\{vidPid}\1&2D9A1B3C&0&0000", null, $@"HID\{vidPid}", @"HID\HIDMaestro"),
            Hop(@"ROOT\HIDCLASS\HM_0000", null, $@"root\{vidPid}", @"root\HIDMaestro"),
            Hop(@"HTREE\ROOT\0", null),
        };

        private static AncestryHop[] OnARealPort(string vidPid, string usbDevice = "6&3B4C5D6E&0&2") => new[]
        {
            Hop($@"HID\{vidPid}\7&2A8B7C1D&0&0000", null, $@"HID\{vidPid}"),
            Hop($@"USB\{vidPid}\{usbDevice}", "HidUsb", $@"USB\{vidPid}"),
            Hop(@"USB\ROOT_HUB30\5&1A2B3C4D&0&0", "USBHUB3", @"USB\ROOT_HUB30"),
            Hop(@"PCI\VEN_8086&DEV_A36D\3&11583659&0&A0", "USBXHCI", @"PCI\VEN_8086&DEV_A36D"),
            Hop(@"ACPI\PNP0A08\0", "pci", @"ACPI\PNP0A08"),
        };

        /// <summary>The hops, with a failure past the last: the walk stops at
        /// the node that settles it, and each further node costs the polling
        /// thread a round of device-tree reads.</summary>
        private static IEnumerable<AncestryHop> AndNoFurther(params AncestryHop[] hops)
        {
            foreach (var hop in hops) yield return hop;
            throw new Xunit.Sdk.XunitException("the walk read past the node that settled it");
        }

        /// <summary>Two kinds of node carry a HIDMaestro hardware id. The
        /// usbip-win2 emulated host controller carries the stamp
        /// ROOT\HIDMAESTRO_UDE, and it names the transport: a persona sits
        /// under it, and so does a real pad attached over usbip-win2. The
        /// node HIDMaestro creates for a controller on its own driver
        /// carries root\HIDMaestro, and no real device sits under one. The
        /// walk took both for the host controller, so such a controller
        /// read as a pad over USB/IP. It read the parents alone, too, and
        /// never the HID interface's own HID\HIDMaestro.</summary>
        [Fact]
        public void TheWalkTellsHidMaestrosOwnNodeFromTheHostController()
        {
            static UsbipAncestry Walk(params AncestryHop[] hops)
                => HidMaestroAncestry.Classify(hops);
            const UsbipAncestry None = UsbipAncestry.None;
            const UsbipAncestry UnderVhci = UsbipAncestry.UnderVhci;
            const UsbipAncestry Persona = UsbipAncestry.Persona;
            const UsbipAncestry OwnDriver = UsbipAncestry.HidMaestroNode;

            // A controller on HIDMaestro's own driver: the first Pro
            // Controller and a DualShock 4, ids no composite profile has.
            Assert.Equal(OwnDriver, Walk(OnHidMaestrosDriver("VID_057E&PID_2009")));
            Assert.Equal(OwnDriver, Walk(OnHidMaestrosDriver("VID_054C&PID_05C4")));
            // Told on the interface's own node, with nothing above it read.
            Assert.Equal(OwnDriver, Walk(
                Hop(@"HID\VID_054C&PID_05C4\1&2D9A1B3C&0&0000", null, @"HID\VID_054C&PID_05C4", @"HID\HIDMaestro")));
            Assert.Equal(OwnDriver, HidMaestroAncestry.Classify(AndNoFurther(
                Hop(@"HID\VID_054C&PID_05C4\1&2D9A1B3C&0&0000", null, @"HID\VID_054C&PID_05C4", @"HID\HIDMaestro"))));
            // And on the node above, where the interface's own ids say nothing.
            Assert.Equal(OwnDriver, Walk(
                Hop(@"HID\VID_054C&PID_05C4\1&2D9A1B3C&0&0000", null, @"HID\VID_054C&PID_05C4"),
                Hop(@"ROOT\HIDCLASS\HM_0000", null, @"root\VID_054C&PID_05C4", @"root\HIDMaestro")));
            // The Xbox profiles' nodes: one of its own, and the gamepad
            // companion xinputhid binds to.
            Assert.Equal(OwnDriver, Walk(
                Hop(@"HID\VID_045E&PID_028E&IG_00\1&2D9A1B3C&0&0000", null, @"HID\VID_045E&PID_028E&IG_00"),
                Hop(@"ROOT\VID_045E&PID_028E&IG_00\HM_0000", null, @"root\VID_045E&PID_028E&IG_00", @"root\HIDMaestro")));
            Assert.Equal(OwnDriver, Walk(
                Hop(@"HID\HIDMAESTRO_VID_045E_PID_0B13&IG_00\1&2D9A1B3C&0&0000", null, @"HID\VID_045E&PID_0B13&IG_00"),
                Hop(@"SWD\HIDMAESTRO_VID_045E_PID_0B13&IG_00\HM_0000", null,
                    @"root\VID_045E&PID_0B13&IG_00", @"root\HIDMaestroGamepad", @"root\HIDMaestro")));

            // The host controller, by its stamp, by its service, or by both:
            // a pad attached over usbip-win2, named by its port or by a
            // serial of its own, and the Steam Deck persona's captured one.
            foreach (var (stamped, serviceReads) in new[] { (true, true), (true, false), (false, true) })
            {
                Assert.Equal(UnderVhci, Walk(OverUsbip("VID_057E&PID_2069", "6&3B4C5D6E&0&1", stamped, serviceReads)));
                Assert.Equal(UnderVhci, Walk(OverUsbip("VID_28DE&PID_1205", "FX1A00000002", stamped, serviceReads)));
                // Below a USB device named by HIDMaestro's serial: a persona.
                Assert.Equal(Persona, Walk(OverUsbip("VID_057E&PID_2069", "HMB123712A734A", stamped, serviceReads)));
            }
            // A persona with one interface, its HID node right below its USB device.
            Assert.Equal(Persona, Walk(
                Hop(@"HID\VID_28DE&PID_1302\7&2A8B7C1D&0&0000", null, @"HID\VID_28DE&PID_1302"),
                Hop(@"USB\VID_28DE&PID_1302\HMABADF10583F0", "HidUsb", @"USB\VID_28DE&PID_1302"),
                Hop(@"USB\ROOT_HUB30\5&1A2B3C4D&0&0", "USBHUB3", @"USB\ROOT_HUB30"),
                Hop(@"ROOT\USB\0000", "usbip2_ude", @"ROOT\USBIP_WIN2\UDE", @"ROOT\HIDMAESTRO_UDE")));
            // Hardware ids compare without case, the stamp's too. Read with
            // case, a stamp spelled another way named a HIDMaestro node, and
            // every pad attached over usbip-win2 was passed over for good.
            Assert.Equal(UnderVhci, Walk(
                Hop(@"HID\VID_057E&PID_2069\7&2A8B7C1D&0&0000", null, @"HID\VID_057E&PID_2069"),
                Hop(@"ROOT\USB\0000", null, @"ROOT\USBIP_WIN2\UDE", @"root\HidMaestro_UDE")));
            // The walk ends at the host controller.
            var forwarded = OverUsbip("VID_057E&PID_2069", "6&3B4C5D6E&0&1", stamped: true, serviceReads: true);
            Assert.Equal(UnderVhci, HidMaestroAncestry.Classify(AndNoFurther(forwarded[..5])));

            // A HIDMaestro id beside the stamp still names a HIDMaestro node.
            Assert.Equal(OwnDriver, Walk(
                Hop(@"HID\VID_054C&PID_05C4\1&2D9A1B3C&0&0000", null, @"HID\VID_054C&PID_05C4"),
                Hop(@"ROOT\USB\0000", "usbip2_ude", @"ROOT\USBIP_WIN2\UDE", @"ROOT\HIDMAESTRO_UDE", @"root\HIDMaestro")));

            // A real port, a host controller that says nothing, a USB device
            // named like a persona's on a real port, and no tree at all.
            Assert.Equal(None, Walk(OnARealPort("VID_054C&PID_05C4")));
            Assert.Equal(None, Walk(OverUsbip("VID_057E&PID_2069", "6&3B4C5D6E&0&1", stamped: false, serviceReads: false)));
            Assert.Equal(None, Walk(OnARealPort("VID_054C&PID_0CE6", "HM0123456789AB")));
            Assert.Equal(None, Walk());
            Assert.Equal(None, HidMaestroAncestry.Classify(null));
        }

        /// <summary>The walk runs on the polling thread, so it is bounded:
        /// the device's own node and the eight above it, whatever the tree
        /// answers. A host controller on the ninth node is found, one on
        /// the tenth is out of reach, and a tree that goes on is not asked
        /// for a tenth node.</summary>
        [Fact]
        public void TheWalkReadsItsOwnNodeAndEightAboveItAndNoMore()
        {
            Assert.Equal(9, HidMaestroAncestry.MaxHops);
            var host = Hop(@"ROOT\USB\0000", "usbip2_ude", @"ROOT\USBIP_WIN2\UDE", @"ROOT\HIDMAESTRO_UDE");
            // The device's own node under a chain of hubs.
            AncestryHop[] Chain(int nodes)
            {
                var chain = new List<AncestryHop>
                {
                    Hop(@"HID\VID_057E&PID_2069\7&2A8B7C1D&0&0000", null, @"HID\VID_057E&PID_2069"),
                };
                for (int hub = 1; hub < nodes; hub++)
                    chain.Add(Hop($@"USB\VID_05E3&PID_0610\6&6E5FE31&0&{hub}", "USBHUB3", @"USB\VID_05E3&PID_0610"));
                return chain.ToArray();
            }

            Assert.Equal(UsbipAncestry.UnderVhci,
                HidMaestroAncestry.Classify(Chain(8).Append(host)));
            Assert.Equal(UsbipAncestry.None,
                HidMaestroAncestry.Classify(Chain(9).Append(host)));
            Assert.Equal(UsbipAncestry.None,
                HidMaestroAncestry.Classify(AndNoFurther(Chain(9))));
        }

        /// <summary>The identity of an XInput pad (StableXInputInstance)
        /// reads these trees by the walk's rule and gives Step 1's answers:
        /// a node of HIDMaestro's own driver and a persona are HIDMaestro's
        /// whatever is in scope, a device under the host controller alone
        /// only while a persona of its ids is, and a device on a real port
        /// never.</summary>
        [Fact]
        public void TheXInputIdentityReadsTheseTreesAsTheWalkDoes()
        {
            var trees = new List<AncestryHop[]>
            {
                OnHidMaestrosDriver("VID_057E&PID_2009"),
                OnHidMaestrosDriver("VID_054C&PID_05C4"),
                OnARealPort("VID_054C&PID_05C4"),
                OnARealPort("VID_054C&PID_0CE6", "HM0123456789AB"),
                OverUsbip("VID_057E&PID_2069", "6&3B4C5D6E&0&1", stamped: false, serviceReads: false),
            };
            foreach (var (stamped, serviceReads) in new[] { (true, true), (true, false), (false, true) })
            {
                trees.Add(OverUsbip("VID_057E&PID_2069", "6&3B4C5D6E&0&1", stamped, serviceReads));
                trees.Add(OverUsbip("VID_28DE&PID_1205", "FX1A00000002", stamped, serviceReads));
                trees.Add(OverUsbip("VID_057E&PID_2069", "HMB123712A734A", stamped, serviceReads));
            }

            var seen = new HashSet<UsbipAncestry>();
            foreach (var tree in trees)
            {
                var walk = HidMaestroAncestry.Classify(tree);
                seen.Add(walk);
                IReadOnlyList<AncestryHop> Hops(string id)
                    => string.Equals(id, tree[0].InstanceId, StringComparison.OrdinalIgnoreCase) ? tree : null;
                foreach (bool inScope in new[] { false, true })
                {
                    bool step1 = walk is UsbipAncestry.Persona or UsbipAncestry.HidMaestroNode
                        || (walk == UsbipAncestry.UnderVhci && inScope);
                    Assert.True(step1 == StableXInputInstance.IsHidMaestroInstance(tree[0].InstanceId, () => inScope, Hops),
                        $"{tree[0].InstanceId}: the walk reads {walk}, scope {inScope}");
                }
            }
            Assert.Equal(Enum.GetValues<UsbipAncestry>().Length, seen.Count);
        }

        /// <summary>Step 1's test on those trees, the walk's own rule in
        /// place of the device tree. A controller on HIDMaestro's own driver
        /// is PadForge's own whatever its ids and whatever is in scope, and
        /// it is never released: no clock lets it back in, though its node
        /// stays in the tree. Read as a pad over USB/IP, it was let through
        /// unless a composite persona of its ids was in scope, and one
        /// passed over that way was opened again 10 s after the persona
        /// went. A pad under the host controller follows the scope as
        /// before, one below a persona's serial is passed over, and a real
        /// port is let through.</summary>
        [Fact]
        public void AControllerOnHidMaestrosOwnDriverIsPassedOverWhateverItsIdsAndTheScope()
        {
            var composite = Profile(Full);
            ushort vid = composite.VendorId, pid = composite.ProductId;
            const string hid = "#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            const string dualShock4 = @"\\?\hid#vid_054c&pid_05c4#1&2d9a1b3c&0&0000" + hid;
            const string switchPro = @"\\?\hid#vid_057e&pid_2009#1&2d9a1b3c&0&0001" + hid;
            const string dualSense = @"\\?\hid#vid_054c&pid_0ce6#1&2d9a1b3c&0&0002" + hid;
            const string forwarded = @"\\?\hid#vid_057e&pid_2069&mi_03#a-forwarded-pad" + hid;
            const string persona = @"\\?\hid#vid_057e&pid_2069&mi_03#a-persona" + hid;
            const string onAPort = @"\\?\hid#vid_057e&pid_2069#on-a-real-port" + hid;
            var trees = new Dictionary<string, AncestryHop[]>
            {
                [dualShock4] = OnHidMaestrosDriver("VID_054C&PID_05C4"),
                [switchPro] = OnHidMaestrosDriver("VID_057E&PID_2009"),
                [dualSense] = OnHidMaestrosDriver("VID_054C&PID_0CE6"),
                [forwarded] = OverUsbip("VID_057E&PID_2069", "6&3B4C5D6E&0&1", stamped: true, serviceReads: true),
                [persona] = OverUsbip("VID_057E&PID_2069", "HMB123712A734A", stamped: true, serviceReads: true),
                [onAPort] = OnARealPort("VID_057E&PID_2069"),
            };
            var vc = Deviceless(Full);
            var savedWalk = InputManager.UsbipAncestryOf;
            var savedTree = InputManager.InDeviceTree;
            var held = new InputManager.SelfVirtualSuppressions();
            try
            {
                InputManager.UsbipAncestryOf = path => HidMaestroAncestry.Classify(trees.GetValueOrDefault(path));
                // Every device stays in the device tree throughout.
                InputManager.InDeviceTree = path => true;

                // No composite profile has the first two id pairs, and no
                // persona is in scope for any of the three.
                Assert.DoesNotContain(HMaestroProfileCatalog.AllProfiles, p => p.RequiresUsbipBackend
                    && ((p.VendorId, p.ProductId) == (0x054C, 0x05C4) || (p.VendorId, p.ProductId) == (0x057E, 0x2009)));
                Assert.True(Profile("dualsense-composite").RequiresUsbipBackend);
                Assert.Equal((Profile("dualsense").VendorId, Profile("dualsense").ProductId),
                    (Profile("dualsense-composite").VendorId, Profile("dualsense-composite").ProductId));
                Assert.False(InputManager.ChecksUsbipAncestry(0x054C, 0x05C4));
                Assert.False(InputManager.ChecksUsbipAncestry(0x057E, 0x2009));
                Assert.False(InputManager.ChecksUsbipAncestry(0x054C, 0x0CE6));

                Assert.True(held.PassOver(1, "", dualShock4, 0x054C, 0x05C4));
                Assert.True(held.PassOver(2, "", switchPro, 0x057E, 0x2009));
                Assert.True(held.PassOver(3, "", dualSense, 0x054C, 0x0CE6));
                var listed = new HashSet<uint> { 1, 2, 3 };
                Assert.Null(held.ReleaseLapsed(listed, Far));
                Assert.True(held.Contains(1) && held.Contains(2) && held.Contains(3));

                // Under the host controller with no persona serial: the scope decides.
                Assert.False(held.PassOver(4, "", forwarded, vid, pid));
                Assert.True(held.PassOver(5, "", persona, vid, pid));
                Assert.False(held.PassOver(6, "", onAPort, vid, pid));
                vc.Connect();
                Assert.True(held.PassOver(4, "", forwarded, vid, pid));
                Assert.False(held.PassOver(6, "", onAPort, vid, pid));

                // The persona gone and its grace run: the forwarded pad is
                // opened again, and nothing else is.
                vc.Disconnect();
                listed.UnionWith(new uint[] { 4, 5 });
                Assert.Equal(new uint[] { 4 }, held.ReleaseLapsed(listed, Far));
                Assert.False(held.Contains(4));
                Assert.True(held.Contains(1) && held.Contains(2) && held.Contains(3) && held.Contains(5));
            }
            finally
            {
                InputManager.UsbipAncestryOf = savedWalk;
                InputManager.InDeviceTree = savedTree;
                Forget(vc);
            }
        }

        /// <summary>The walk up from the XUSB interface of a pad xboxgip
        /// serves on the usbip-win2 emulated host controller, as read on
        /// Windows 11 ARM64 on 2026-11-19 (hifihedgehog/SDL#51): the HID
        /// node that carries the interface, the node xboxgip makes, the USB
        /// device, the root hub and the host controller.
        /// <paramref name="usbDevice"/> ends the USB device's instance id,
        /// HIDMaestro's serial for one of its own.</summary>
        private static AncestryHop[] XboxGipOverUsbip(string usbDevice, bool stamped) => new[]
        {
            Hop(@"HID\VID_045E&PID_02FF&IG_00\4&107E31D5&0&0000", null, @"HID\VID_045E&PID_02FF&IG_00"),
            Hop(@"USB\VID_045E&PID_02FF&IG_00\00&00&0000FFFBDF0FD306", "HidUsb", @"USB\VID_045E&PID_02FF&IG_00"),
            Hop($@"USB\VID_045E&PID_0B13\{usbDevice}", "dc1-controller", @"USB\VID_045E&PID_0B13"),
            Hop(@"USB\ROOT_HUB30\1&2B53A856&0&0", "USBHUB3", @"USB\ROOT_HUB30"),
            Hop(@"ROOT\USB\0000", "usbip2_ude",
                stamped ? new[] { @"ROOT\USBIP_WIN2\UDE", @"ROOT\HIDMAESTRO_UDE" } : new[] { @"ROOT\USBIP_WIN2\UDE" }),
            Hop(@"HTREE\ROOT\0", null),
        };

        /// <summary>A joystick of SDL's XInput backend is named "XInput#N"
        /// and nothing else, so the guard walks from the XUSB interface
        /// OpenXInput says serves the number. Under a host controller with
        /// no stamp neither fork has anything to find, and OpenXInput gives
        /// every XUSB interface a slot, a persona's too: HIDMaestro's Xbox
        /// One and Series controllers on its USB/IP driver are served by
        /// xboxgip and reach SDL that way. One below HIDMaestro's serial is
        /// passed over whatever is in scope, under either kind of host
        /// controller, and is never released. A pad attached over
        /// usbip-win2 with a serial of its own is let through, and so is a
        /// pad on a real port. A number OpenXInput has no interface for is
        /// let through, as every such joystick was before.</summary>
        [Fact]
        public void AnXInputJoystickIsWalkedFromTheInterfaceThatServesItsNumber()
        {
            const string xusb = "#{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}";
            const string persona = @"\\?\hid#vid_045e&pid_02ff&ig_00#a-persona" + xusb;
            const string stampedPersona = @"\\?\hid#vid_045e&pid_02ff&ig_00#a-persona-under-a-stamp" + xusb;
            const string forwarded = @"\\?\hid#vid_045e&pid_02ff&ig_00#a-forwarded-pad" + xusb;
            const string onAPort = @"\\?\hid#vid_045e&pid_02ff&ig_00#on-a-real-port" + xusb;
            var trees = new Dictionary<string, AncestryHop[]>
            {
                [persona] = XboxGipOverUsbip("HM7A1FDF0FD306", stamped: false),
                [stampedPersona] = XboxGipOverUsbip("HM7A1FDF0FD306", stamped: true),
                [forwarded] = XboxGipOverUsbip("3032363030303236", stamped: false),
                [onAPort] = OnARealPort("VID_045E&PID_02FF"),
            };
            var served = new Dictionary<int, XInputSlotIdentity.Answer>
            {
                [0] = new(XInputSlotIdentity.Kind.Found, persona),
                [1] = new(XInputSlotIdentity.Kind.Found, forwarded),
                [2] = new(XInputSlotIdentity.Kind.Found, onAPort),
                [3] = new(XInputSlotIdentity.Kind.Busy),
                [4] = new(XInputSlotIdentity.Kind.None),
                [5] = new(XInputSlotIdentity.Kind.Unsupported),
                [6] = new(XInputSlotIdentity.Kind.Found, stampedPersona),
            };
            var walked = new List<string>();
            var savedWalk = InputManager.UsbipAncestryOf;
            var savedTree = InputManager.InDeviceTree;
            var held = new InputManager.SelfVirtualSuppressions();
            try
            {
                XInputSlotIdentity.AnswerForTest = slot => served[slot];
                InputManager.UsbipAncestryOf = path =>
                {
                    walked.Add(path);
                    return HidMaestroAncestry.Classify(path == null ? null : trees.GetValueOrDefault(path));
                };
                InputManager.InDeviceTree = path => true;
                // No persona of these ids is in scope, so nothing here is
                // passed over on its place under the host controller alone.
                Assert.False(InputManager.ChecksUsbipAncestry(0x045E, 0x02FF));

                Assert.True(held.PassOver(1, "", "XInput#0", 0x045E, 0x02FF));
                Assert.False(held.PassOver(2, "", "XInput#1", 0x045E, 0x02FF));
                Assert.False(held.PassOver(3, "", "XInput#2", 0x045E, 0x02FF));
                Assert.False(held.PassOver(4, "", "XInput#3", 0x045E, 0x02FF));
                Assert.False(held.PassOver(5, "", "XInput#4", 0x045E, 0x02FF));
                Assert.False(held.PassOver(6, "", "XInput#5", 0x045E, 0x02FF));
                Assert.True(held.PassOver(7, "", "XInput#6", 0x045E, 0x02FF));
                Assert.Equal(new[] { persona, forwarded, onAPort, null, null, null, stampedPersona }, walked);

                // HIDMaestro's serial settles it for as long as SDL lists
                // the joystick: no clock lets it back in.
                var listed = new HashSet<uint> { 1, 7 };
                Assert.Null(held.ReleaseLapsed(listed, Far));
                Assert.True(held.Contains(1) && held.Contains(7));

                // The path the walk starts from: the interface behind a
                // number, nothing for a number with none, and any other
                // path as it is.
                Assert.Equal(persona, InputManager.AncestryPathOf("XInput#0"));
                Assert.Null(InputManager.AncestryPathOf("XInput#3"));
                Assert.Null(InputManager.AncestryPathOf("XInput#4"));
                Assert.Null(InputManager.AncestryPathOf("XInput#5"));
                Assert.Equal(onAPort, InputManager.AncestryPathOf(onAPort));
                Assert.Null(InputManager.AncestryPathOf(null));
            }
            finally
            {
                XInputSlotIdentity.AnswerForTest = null;
                InputManager.UsbipAncestryOf = savedWalk;
                InputManager.InDeviceTree = savedTree;
            }
        }

        /// <summary>Once the clock puts a passed-over device out of scope,
        /// Step 1 opens it again only while it is in the device tree. A
        /// persona that has left the tree is listed by SDL a moment longer,
        /// and opened then it became a controller of the user's that was
        /// never there. It stays passed over until SDL drops it. A pad still
        /// in the tree is opened and read, as before.</summary>
        [Fact]
        public void ADeviceGoneFromTheTreeIsNotOpenedAgainWhenItsScopeLapses()
        {
            var owner = new object();
            var savedTree = InputManager.InDeviceTree;
            long now = Environment.TickCount64;
            var held = new InputManager.SelfVirtualSuppressions();
            var gone = new HashSet<string>();
            try
            {
                InputManager.InDeviceTree = path => !gone.Contains(path);
                InputManager.NotePersonaLive(owner, Profile(Full).VendorId, Profile(Full).ProductId);
                held.Add(7, 0x057E, 0x2069, byAncestryOnly: true, devicePath: "a persona's");
                held.Add(8, 0x057E, 0x2069, byAncestryOnly: true, devicePath: "a forwarded pad's");
                var listed = new HashSet<uint> { 7, 8 };
                InputManager.NotePersonaRetired(owner, now);
                Assert.Null(held.ReleaseLapsed(listed, now + 1));

                // The grace has run: the persona's device is gone, the pad is there.
                gone.Add("a persona's");
                long lapsed = now + InputManager.PersonaRetireGraceMs;
                Assert.Equal(new uint[] { 8 }, held.ReleaseLapsed(listed, lapsed));
                Assert.True(held.Contains(7));
                Assert.False(held.Contains(8));
                Assert.Null(held.ReleaseLapsed(listed, lapsed));
                Assert.True(held.Contains(7));

                // SDL drops it: forgotten, with nothing to open.
                listed.Remove(7);
                Assert.Null(held.ReleaseLapsed(listed, lapsed));
                Assert.False(held.Contains(7));
            }
            finally
            {
                InputManager.InDeviceTree = savedTree;
                InputManager.NotePersonaRetired(owner, now);
                InputManager.ChecksUsbipAncestry(0, 0, now + InputManager.PersonaRetireGraceMs);
            }
        }

        /// <summary>A source file's lines of code, trimmed, with the blank
        /// lines and the comment lines left out.</summary>
        private static string[] Code(string text) => text.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal)).ToArray();

        /// <summary>The live walk reads the device tree, so no test runs it.
        /// These are the lines that hand the tree to the tested rule: the
        /// seam's default is the walk, the walk classifies the hops it
        /// reads, the first hop is the device's own node, read before any
        /// parent is asked for, and the read stops at the rule's own
        /// bound.</summary>
        [Fact]
        public void TheLiveWalkHandsTheDevicesOwnNodeToTheRuleFirst()
        {
            var guard = Code(AuditDelta20261002Tests.RepoText(
                "PadForge.App", "Common", "Input", "InputManager.Step1.UsbipVhciGuard.cs"));
            Assert.Contains("internal static Func<string, UsbipAncestry> UsbipAncestryOf = WalkUsbipAncestry;", guard);

            int walk = Array.IndexOf(guard, "private static UsbipAncestry WalkUsbipAncestry(string hidDevicePath)");
            Assert.True(walk >= 0);
            Assert.Equal("=> TryLocateDevNode(hidDevicePath, out uint devInst)", guard[walk + 1]);
            Assert.Equal("? HidMaestroAncestry.Classify(ReadAncestryHops(devInst))", guard[walk + 2]);
            Assert.Equal(": UsbipAncestry.None;", guard[walk + 3]);

            int read = Array.IndexOf(guard, "private static IEnumerable<AncestryHop> ReadAncestryHops(uint devInst)");
            Assert.True(read >= 0);
            int own = Array.FindIndex(guard, read, l => l.StartsWith("yield return new AncestryHop(", StringComparison.Ordinal));
            int parent = Array.FindIndex(guard, read, l => l.Contains("CM_Get_Parent(", StringComparison.Ordinal));
            Assert.True(own > read && parent > own, "the device's own node is read before its first parent");
            Assert.Equal("for (int read = 1; ; read++)", guard[own - 2]);
            Assert.Equal("GetDevNodeInstanceId(devInst), GetDevNodeHardwareIds(devInst), GetDevNodeService(devInst));",
                guard[own + 1]);
            Assert.Equal("if (read == HidMaestroAncestry.MaxHops || CM_Get_Parent(out uint parent, devInst, 0) != CR_SUCCESS)",
                guard[own + 2]);
            Assert.Equal("yield break;", guard[own + 3]);
            Assert.Equal("devInst = parent;", guard[own + 4]);
        }

        /// <summary>Step 1 opens real devices through SDL, so no test runs
        /// its loop. These are the lines that hand it to the tested parts:
        /// the release before any device is opened, and the test of each
        /// device between its open and its record, with the two lines that
        /// act on its answer. A device passed over is closed on the polling
        /// thread, and the loop goes on to the next one. With no close, SDL's
        /// handle on PadForge's own virtual stayed open until the wrapper's
        /// finalizer closed it on another thread. With no skip, Step 1
        /// recorded the device it had just closed. Disconnect retires the
        /// persona after HIDMaestro has disposed the controller, never
        /// before: the device leaves the tree after the dispose, and the
        /// grace runs from there. Comment lines do not count.</summary>
        [Fact]
        public void Step1AndDisconnectCallTheGuardWhereItCounts()
        {
            var step1 = Code(AuditDelta20261002Tests.RepoText(
                "PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs"));
            int release = Array.FindIndex(step1, l => l.Contains(
                "_suppressedSelfVirtualIds.ReleaseLapsed(currentInstanceIds, Environment.TickCount64)", StringComparison.Ordinal));
            int loop = Array.IndexOf(step1, "foreach (uint instanceId in joystickIds)");
            int skip = Array.FindIndex(step1, l => l == "if (_suppressedSelfVirtualIds.Contains(instanceId))");
            int open = Array.IndexOf(step1, "if (!wrapper.Open(instanceId))");
            int test = Array.FindIndex(step1, l => l.StartsWith(
                "if (_suppressedSelfVirtualIds.PassOver(instanceId, wrapper.SerialNumber,", StringComparison.Ordinal));
            int record = Array.FindIndex(step1, l => l.StartsWith(
                "UserDevice ud = FindOrCreateUserDevice(wrapper.InstanceGuid", StringComparison.Ordinal));
            Assert.True(release >= 0 && loop > release, "the release runs before Step 1 opens a device");
            Assert.True(skip > loop && open > skip, "a passed-over id is skipped before it is opened");
            Assert.True(test > open && record > test, "each opened device is tested before it is recorded");
            Assert.Equal(new[]
            {
                "wrapper.DevicePath, wrapper.VendorId, wrapper.ProductId))",
                "{",
                "wrapper.Dispose();",
                "continue;",
                "}",
            }, step1[(test + 1)..(test + 6)]);

            var vc = Code(AuditDelta20261002Tests.RepoText(
                "PadForge.App", "Common", "Input", "HMaestroVirtualController.cs"));
            int disconnect = Array.IndexOf(vc, "public void Disconnect()");
            Assert.True(disconnect >= 0);
            int dispose = Array.IndexOf(vc, "_controller?.Dispose();", disconnect);
            int retire = Array.IndexOf(vc,
                "if (_profile.RequiresUsbipBackend) InputManager.NotePersonaRetired(this);", disconnect);
            Assert.True(dispose > disconnect && retire > dispose,
                "Disconnect retires the persona after the SDK controller is disposed");
        }

        /// <summary>The persona's ids are the ones its profile declares, the
        /// ids Step 1 reads off the device it opens.</summary>
        [Fact]
        public void ThePersonaProfileIsAUsbIpPersonaUnderTheSwitch2ProIds()
        {
            Assert.True(Profile(Full).RequiresUsbipBackend);
            Assert.Equal((ushort)0x057E, Profile(Full).VendorId);
            Assert.Equal((ushort)0x2069, Profile(Full).ProductId);
            Assert.False(Profile(Plain).RequiresUsbipBackend);
        }
    }
}
