using System.Reflection;
using System.Runtime.InteropServices;
using HIDMaestro;
using HIDMaestro.Internal;
using PadForge.Common.Input;
using PadForge.Engine;

namespace PadForge.Tools.OutputFamilyProbe
{
    /// <summary>Records what a controller hands HIDMaestro, in place of the
    /// SDK controller (HMaestroVirtualController.SubmitTargetSeam, the seam
    /// PadForge's own tests record frames through).</summary>
    internal sealed class Recorder : IHMaestroSubmitTarget
    {
        public readonly object Lock = new();
        public HMGamepadState? State;
        public byte[] Raw;
        public bool RawCarriesId;

        public void SubmitState(in HMGamepadState state)
        {
            var copy = state;
            if (state.Axes != null) copy.Axes = new Dictionary<HMAxis, float>(state.Axes);
            lock (Lock) State = copy;
        }

        public void SubmitRawReport(ReadOnlySpan<byte> report) { lock (Lock) { Raw = report.ToArray(); RawCarriesId = false; } }

        public void SubmitRawExtendedReport(ReadOnlySpan<byte> report) { lock (Lock) { Raw = report.ToArray(); RawCarriesId = true; } }
    }

    /// <summary>Records what a virtual keyboard or mouse would send
    /// (KeyboardMouseVirtualController.CreateSinkSeam).</summary>
    internal sealed class KbmRecorder : IKbmHidSink
    {
        public readonly List<string> Log = new();
        public void SubmitKeyboard(HMKeyModifiers modifiers, HMKey[] keys)
        { lock (Log) Log.Add("keys " + string.Join(",", keys.Select(k => ((int)k).ToString("X2")))); }
        public void SubmitConsumer(ushort[] usages) { lock (Log) Log.Add("consumer " + usages.Length); }
        public void SubmitMouse(in HMMouseState state) { lock (Log) Log.Add($"mouse {state.X},{state.Y}"); }
        public HMKeyboardLeds? KeyboardLeds => null;
#pragma warning disable CS0067
        public event Action<HMKeyboardLeds> KeyboardLedsChanged;
#pragma warning restore CS0067
        public void Dispose() { }
    }

    /// <summary>Every family's comparison run on what PadForge's own code
    /// makes for the known state, with no device: the same Submitter pass the
    /// run with rights makes, into a controller whose frames go to a
    /// recorder. A frame is turned into the bytes Windows hands a reader (the
    /// report id HIDMaestro heads a data-only frame with), a state into the
    /// report HIDMaestro's own encoder makes of it where the profile defines
    /// one, and each must PASS, and the same bytes shifted by one must
    /// FAIL. Two reports the device builds itself are laid out by hand from
    /// their reader instead: dualshock-3-full's joystick report by DsHidMini
    /// and the Switch 2 Pro composite's report 0x09 at SDL's offsets, Y up
    /// high. The XInput read is checked to go through Windows' own
    /// xinput1_4.dll, not the fork beside the probe.</summary>
    internal static class SelfCheck
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        internal static int Run(Action<string> log)
        {
            int failed = 0;
            void Line(string name, bool ok, string text)
            {
                if (!ok) failed++;
                log($"{(ok ? "PASS" : "FAIL")} {name}: {text}");
            }

            string missing = PadForgePath.Resolve();
            Line("PadForge members reached by name", missing == null, missing == null ? "CreateVirtualController, CaptureExtendedBuild, DisposeHMaestroContextOnShutdown(IReadOnlyList<Task>), HMaestroVirtualController._profile" : "missing " + missing);
            string sdk = ResolveSdk();
            Line("HIDMaestro members reached by name", sdk == null, sdk ?? "VendorBlobCodec.EncodeInput, HMController.ResolveCanonicalAxis, HMController.ResolveTrigger");

            var xs = new Native.XINPUT_STATE { Gamepad = new Native.XINPUT_GAMEPAD { wButtons = 0x1000, sThumbLX = 32767, sThumbLY = 16384, sThumbRX = -8192, bLeftTrigger = 255 } };
            Line("xinput comparison, the known state", Expect.XInput(in xs).Pass, Expect.XInput(in xs).ToString());
            var swapped = xs;
            (swapped.Gamepad.sThumbLX, swapped.Gamepad.sThumbLY) = (swapped.Gamepad.sThumbLY, swapped.Gamepad.sThumbLX);
            Line("xinput comparison, X and Y swapped", !Expect.XInput(in swapped).Pass, Expect.XInput(in swapped).ToString());
            // The read goes through the copy a game loads, never through the
            // OpenXInput fork PadForge.App puts beside the probe, which skips
            // HIDMaestro's controllers.
            string xinputRead = Native.ModulePath(Native.XInputModule);
            bool fromSystem = string.Equals(System.IO.Path.GetDirectoryName(xinputRead), Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase);
            string beside = System.IO.Path.Combine(AppContext.BaseDirectory, "xinput1_4.dll");
            bool apart = true;
            string besideText = "no copy beside the probe";
            if (System.IO.File.Exists(beside))
            {
                IntPtr byPath = NativeLibrary.Load(beside);
                apart = byPath != Native.XInputModule;
                besideText = $"the copy beside the probe, {beside}, is {(apart ? "a module of its own" : "the same module")}";
            }
            Line("XInput read through Windows' own xinput1_4.dll", fromSystem && apart, $"{xinputRead}; {besideText}");

            CheckEveryFixedLayoutProfile(Line);
            foreach (var finding in FaceButtonFindings())
                log("FINDING for HIDMaestro, not a probe failure: " + finding);

            // dualshock-3-full's id-less joystick report for the known state,
            // laid out by DsHidMini's DS3_RAW_TO_SIXAXIS_HID_INPUT_REPORT
            // (driver/DsHid.c 585-676) by hand, not by HIDMaestro's program:
            // Cross and L2 in data byte 0, the hat at rest, the sticks, Circle
            // and Cross pressure inverted at 8 and 9, L2 and R2 inverted at 10
            // and 11.
            byte[] sixaxis = { 0x00, 0x14, 0x00, 0x00, 0x08, 0xFF, 0x40, 0x60, 0x80, 0xFF, 0x00, 0x00, 0xFF };
            var sixaxisVerdict = Expect.SixaxisJoystick(sixaxis, KnownState.GamepadButtons);
            Line("playstation:dualshock-3-full joystick report as DsHidMini lays it out", sixaxisVerdict.Pass, $"{sixaxisVerdict} | {Expect.Hex(sixaxis)}");
            var sixaxisLate = new byte[sixaxis.Length];
            Array.Copy(sixaxis, 0, sixaxisLate, 1, sixaxis.Length - 1);
            Line("playstation:dualshock-3-full the same report one byte late", !Expect.SixaxisJoystick(sixaxisLate, KnownState.GamepadButtons).Pass,
                Expect.SixaxisJoystick(sixaxisLate, KnownState.GamepadButtons).ToString());
            var sixaxisUninverted = (byte[])sixaxis.Clone();
            (sixaxisUninverted[11], sixaxisUninverted[12]) = (0xFF, 0x00);
            Line("playstation:dualshock-3-full the triggers written uninverted", !Expect.SixaxisJoystick(sixaxisUninverted, KnownState.GamepadButtons).Pass,
                Expect.SixaxisJoystick(sixaxisUninverted, KnownState.GamepadButtons).ToString());

            foreach (var f in Families.All())
            {
                try
                {
                    switch (f.Route)
                    {
                        case Route.Keyboard: CheckKeyboard(f, Line); break;
                        case Route.Mouse: CheckMouse(f, Line); break;
                        default: CheckController(f, Line); break;
                    }
                }
                catch (Exception ex)
                {
                    Line(f.Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
                }
            }
            return failed;
        }

        private static MethodInfo s_encode, s_canonical, s_trigger;
        private static Type s_encoderState;

        private static string ResolveSdk()
        {
            var sdk = typeof(HMContext).Assembly;
            var codec = sdk.GetType("HIDMaestro.Internal.VendorBlobCodec");
            s_encode = codec?.GetMethod("EncodeInput", Any);
            s_encoderState = codec?.GetNestedType("EncoderState", Any);
            var controller = typeof(HMController);
            s_canonical = controller.GetMethod("ResolveCanonicalAxis", Any);
            s_trigger = controller.GetMethod("ResolveTrigger", Any);
            if (s_encode == null || s_encoderState == null) return "missing VendorBlobCodec.EncodeInput";
            if (s_canonical == null || s_trigger == null) return "missing HMController.ResolveCanonicalAxis or ResolveTrigger";
            return null;
        }

        /// <summary>The six values HMController.SubmitState resolves before it
        /// encodes, as PadForge.Tests Switch2ProCompositeTests.Resolved reads
        /// them: each declared stick axis, 0.5 when the state has none, and the
        /// two triggers through ResolveTrigger on ResolveCanonicalAxis.</summary>
        private static float[] Resolved(HMProfile p, in HMGamepadState s)
        {
            var axes = s.Axes;
            float Axis(HMAxis a) => axes != null && a != HMAxis.None && axes.TryGetValue(a, out float v) ? Math.Clamp(v, 0f, 1f) : 0.5f;
            var sticks = p.Sticks;
            var lt = (HMAxis)s_canonical.Invoke(null, new object[] { p.AxisMap, "lefttrigger", HMAxis.Z });
            var rt = (HMAxis)s_canonical.Invoke(null, new object[] { p.AxisMap, "righttrigger", HMAxis.Rz });
            float T(int i, HMAxis c) => Convert.ToSingle(s_trigger.Invoke(null, new object[] { axes, p.Triggers, i, c }));
            return new[]
            {
                sticks.Count > 0 ? Axis(sticks[0].XAxis) : 0.5f, sticks.Count > 0 ? Axis(sticks[0].YAxis) : 0.5f,
                sticks.Count > 1 ? Axis(sticks[1].XAxis) : 0.5f, sticks.Count > 1 ? Axis(sticks[1].YAxis) : 0.5f,
                T(0, lt), T(1, rt),
            };
        }

        /// <summary>The report HIDMaestro's VendorBlobCodec.EncodeInput makes of
        /// a state for a profile's extendedReport.</summary>
        private static byte[] Encode(ExtendedReportSpec spec, HMProfile p, HMGamepadState s)
        {
            var v = Resolved(p, s);
            var buffer = new byte[spec.Size];
            object counters = Activator.CreateInstance(s_encoderState, true);
            s_encode.Invoke(null, new object[] { spec, s, v[0], v[1], v[2], v[3], v[4], v[5], buffer, counters });
            return buffer;
        }

        private static HMaestroVirtualController Seamed(Family f, HMProfile profile, Recorder rec)
        {
            var vc = Families.Unconnected(profile, f.Type);
            vc.CreateControllerSeam = (_, _) => null;
            vc.SubmitTargetSeam = () => rec;
            vc.Connect();
            return vc;
        }

        /// <summary>The bytes a reader gets for a data-only frame: HIDMaestro
        /// heads it with the report id the profile declares
        /// (HMaestroVirtualController.SubmitRawReport, Step 5's
        /// ExtendedReportPacker.NeedsRawReport branch for the Extended packer, SonyReportPackers' report 0x01).</summary>
        private static byte[] Delivered(Family f, byte[] frame, bool carriesId, HMProfile effective)
        {
            if (carriesId) return frame;
            byte id = f.Route switch
            {
                Route.SInputPacked => SInputReportPacker.ReportId,
                Route.ExtendedPacked => HMaestroVirtualController.DescriptorHasPidFfbBlock(effective.DescriptorHex) ? (byte)1 : (byte)0,
                _ => f.Spec?.ReportIdByte ?? 0,
            };
            var d = new byte[frame.Length + 1];
            d[0] = id;
            frame.CopyTo(d, 1);
            return d;
        }

        /// <summary>The same report one byte late, every field moved: the
        /// comparison must refuse it.</summary>
        private static byte[] Shifted(byte[] d)
        {
            var s = new byte[d.Length];
            s[0] = d[0];
            for (int i = 2; i < d.Length; i++) s[i] = d[i - 1];
            return s;
        }

        /// <summary>Runs the family's Submitter pass, neutral then the known
        /// state, into a seamed controller, and returns what the known pass
        /// handed HIDMaestro.</summary>
        private static (HMGamepadState? State, byte[] Raw, bool CarriesId) Capture(Family f, HMaestroVirtualController vc, Recorder rec)
        {
            var s = new Submitter(f, vc, new InputManager());
            // The neutral pass first, as a new slot's first frames are, then
            // the known state once the recorder has let go of it. A lane
            // delivers in order (HMaestroSubmitLane).
            s.Pass(true, default);
            for (int i = 0; i < 100; i++)
            {
                lock (rec.Lock) if (rec.State != null || rec.Raw != null) break;
                Thread.Sleep(5);
            }
            lock (rec.Lock) { rec.State = null; rec.Raw = null; }
            s.Pass(false, default);
            for (int i = 0; i < 200; i++)
            {
                lock (rec.Lock)
                    if (rec.State != null || rec.Raw != null) return (rec.State, rec.Raw, rec.RawCarriesId);
                Thread.Sleep(5);
            }
            return (null, null, false);
        }

        /// <summary>What the state comes to on each lane HIDMaestro reads it
        /// through. The descriptor lane reads each axis by its key
        /// (HMController.cs, BuildReportInto "reads the state's axes by key
        /// itself"), the profile's own Sticks and Triggers. The simple-slot
        /// lane, which the XUSB and GIP slices and the extended-report
        /// encoders take, resolves six values first (Resolved). Each lane that
        /// applies must carry the known state.</summary>
        private static List<string> StateProblems(HMProfile p, in HMGamepadState st, HMButton expected, bool simpleSlots)
        {
            var bad = new List<string>();
            var axes = st.Axes;
            var sticks = p.Sticks;
            var triggers = p.Triggers;
            void Key(string name, HMAxis axis, double want, double rest)
            {
                if (axis == HMAxis.None) return;
                double got = axes != null && axes.TryGetValue(axis, out float v) ? v : rest;
                if (Math.Abs(got - want) > KnownState.Tolerance) bad.Add($"{name} key {axis} {got:0.000} want {want:0.000}");
            }
            if (sticks.Count > 0) { Key("LX", sticks[0].XAxis, KnownState.LeftX, 0.5); Key("LY", sticks[0].YAxis, KnownState.LeftY, 0.5); }
            if (sticks.Count > 1) { Key("RX", sticks[1].XAxis, KnownState.RightX, 0.5); Key("RY", sticks[1].YAxis, KnownState.RightY, 0.5); }
            if (triggers.Count > 0) Key("LT", triggers[0].Axis, KnownState.LeftTrigger, 0);
            if (triggers.Count > 1) Key("RT", triggers[1].Axis, KnownState.RightTrigger, 0);
            if (simpleSlots)
            {
                var v = Resolved(p, st);
                var want = new[] { KnownState.LeftX, KnownState.LeftY, KnownState.RightX, KnownState.RightY, KnownState.LeftTrigger, KnownState.RightTrigger };
                string[] names = { "LX", "LY", "RX", "RY", "LT", "RT" };
                for (int i = 0; i < 6; i++)
                {
                    bool declared = i < 4 ? i / 2 < sticks.Count : (i - 4) < triggers.Count;
                    if (declared && Math.Abs(v[i] - want[i]) > KnownState.Tolerance) bad.Add($"{names[i]} slot {v[i]:0.000} want {want[i]:0.000}");
                }
            }
            if (st.Buttons != expected) bad.Add($"buttons {(uint)st.Buttons:X} want {(uint)expected:X}");
            return bad;
        }

        private static string StateText(HMProfile p, in HMGamepadState st)
        {
            var axes = st.Axes;
            string K(HMAxis a) => a == HMAxis.None ? "-" : axes != null && axes.TryGetValue(a, out float v) ? v.ToString("0.000") : "rest";
            var sticks = p.Sticks;
            var triggers = p.Triggers;
            var parts = new List<string> { $"buttons {(uint)st.Buttons:X}" };
            for (int i = 0; i < sticks.Count && i < 2; i++) parts.Add($"stick{i} {sticks[i].XAxis}={K(sticks[i].XAxis)} {sticks[i].YAxis}={K(sticks[i].YAxis)}");
            for (int i = 0; i < triggers.Count && i < 2; i++) parts.Add($"trigger{i} {triggers[i].Axis}={K(triggers[i].Axis)}");
            return string.Join(", ", parts);
        }

        private static void CheckController(Family f, Action<string, bool, string> line)
        {
            var effective = f.Profile;
            var rec = new Recorder();
            var vc = Seamed(f, effective, rec);
            try
            {
                var (state, raw, carriesId) = Capture(f, vc, rec);
                HMButton expected = DeviceRun.ExpectedButtons(f, effective);

                if (f.Route is Route.Gamepad or Route.PlayStationState or Route.RawState)
                {
                    if (state == null) { line(f.Name, false, "no state reached the recorder"); return; }
                    var st = state.Value;
                    // A customized Extended slot builds its own profile from the
                    // slot's layout (Step 5's CreateHMaestroController). The checks below read its
                    // wire through ExtendedLayout.
                    if (!(f.Type == VirtualControllerType.Extended && f.Customize))
                    {
                        bool simple = f.Read == ReadKind.XInput || f.Spec != null;
                        var bad = StateProblems(effective, st, expected, simple);
                        line(f.Name + " state handed to HIDMaestro", bad.Count == 0,
                            bad.Count == 0 ? StateText(effective, st) : string.Join("; ", bad) + " | " + StateText(effective, st));
                    }
                    if (f.Spec != null && BulkStart.Needs(f))
                    {
                        // The composite sends report 0x09 from its own state
                        // block, Y up high, not the codec's encode of the spec
                        // (switch2-pro-composite.json notes). The known state
                        // laid out by hand where SDL's HandleSwitchProState
                        // reads it (SDL_hidapi_switch2.c 1299-1370): A at byte
                        // 3 bit 1, the left stick 4095 and 3071 at bytes 6 to
                        // 8, the right stick 1536 and 2048 at bytes 9 to 11.
                        byte[] report = new byte[64];
                        byte[] head = { 0x09, 0x00, 0x25, 0x02, 0x00, 0x00, 0xFF, 0xFF, 0xBF, 0x00, 0x06, 0x80 };
                        head.CopyTo(report, 0);
                        var verdict = DeviceRun.Evaluate(f, effective, null, report, expected);
                        line(f.Name + $" report {f.Spec.ReportId} as SDL's Switch 2 driver reads it", verdict.Pass, verdict + " | " + Expect.Hex(report, 24));
                        var neg = DeviceRun.Evaluate(f, effective, null, Shifted(report), expected);
                        line(f.Name + " the same report one byte late", !neg.Pass, neg.ToString());
                        var codec = Encode(f.Spec, effective, st);
                        var down = DeviceRun.Evaluate(f, effective, null, codec, expected);
                        line(f.Name + " the codec's encode of the spec, Y up low", !down.Pass, down + " | " + Expect.Hex(codec, 24));
                    }
                    else if (f.Spec != null)
                    {
                        var report = Encode(f.Spec, effective, st);
                        var verdict = DeviceRun.Evaluate(f, effective, null, report, expected);
                        line(f.Name + $" HIDMaestro's encode of report {f.Spec.ReportId}", verdict.Pass, verdict + " | " + Expect.Hex(report, 24));
                        var neg = DeviceRun.Evaluate(f, effective, null, Shifted(report), expected);
                        line(f.Name + " the same report one byte late", !neg.Pass, neg.ToString());
                    }
                    else if (f.Type == VirtualControllerType.Extended && f.Customize)
                    {
                        // The descriptor this slot's build emits is the layout
                        // ExtendedReportPacker packs (ExtendedReportPacker.cs:16-25),
                        // so the packer's frame of the same state is the wire.
                        var buf = new byte[ExtendedReportPacker.MaxReportSize];
                        int n = ExtendedReportPacker.Pack(KnownState.Raw(f.ProfileId, f.Layout, false), f.Layout, buf);
                        var d = new byte[n + 1];
                        d[0] = 0x01;
                        Array.Copy(buf, 0, d, 1, n);
                        var verdict = DeviceRun.Evaluate(f, effective, null, d, expected);
                        line(f.Name + " the layout's wire", verdict.Pass, verdict + " | " + Expect.Hex(d, 24));
                        var neg = DeviceRun.Evaluate(f, effective, null, Shifted(d), expected);
                        line(f.Name + " the same report one byte late", !neg.Pass, neg.ToString());
                    }
                    return;
                }

                if (raw == null) { line(f.Name, false, "no frame reached the recorder"); return; }
                var delivered = Delivered(f, raw, carriesId, effective);
                var ok = DeviceRun.Evaluate(f, effective, null, delivered, expected);
                line(f.Name + " packer frame as Windows delivers it", ok.Pass, ok + " | " + Expect.Hex(delivered, 24));
                var shifted = DeviceRun.Evaluate(f, effective, null, Shifted(delivered), expected);
                line(f.Name + " the same frame one byte late", !shifted.Pass, shifted.ToString());
            }
            finally
            {
                try { vc.Dispose(); } catch { }
            }
        }

        /// <summary>Every Xbox and PlayStation profile in the pickers, not only
        /// the ones the run creates: the state a fixed-layout slot hands
        /// HIDMaestro, read at the profile's own stick and trigger keys. A
        /// profile whose frame a Sony packer builds is checked by its
        /// packer's family instead.</summary>
        private static void CheckEveryFixedLayoutProfile(Action<string, bool, string> line)
        {
            int checkedCount = 0;
            var failures = new List<string>();
            foreach (var (type, list) in new[]
                     {
                         (VirtualControllerType.Xbox, HMaestroProfileCatalog.XboxProfiles),
                         (VirtualControllerType.PlayStation, HMaestroProfileCatalog.PlayStationProfiles),
                     })
            {
                foreach (var p in list)
                {
                    var route = Families.RouteOf(type, p.Id, p.BuildsItsOwnReports, default);
                    if (route == Route.SonyPacked) continue;
                    var f = new Family { Name = p.Id, Type = type, ProfileId = p.Id, Profile = p, Route = route, Read = type == VirtualControllerType.Xbox ? ReadKind.XInput : ReadKind.Hid, Spec = p.ExtendedReport };
                    var rec = new Recorder();
                    HMaestroVirtualController vc = null;
                    try
                    {
                        vc = Seamed(f, p, rec);
                        var (state, _, _) = Capture(f, vc, rec);
                        checkedCount++;
                        if (state == null) { failures.Add($"{p.Id}: no state"); continue; }
                        var bad = StateProblems(p, state.Value, KnownState.GamepadButtons, f.Read == ReadKind.XInput || f.Spec != null);
                        if (bad.Count > 0) failures.Add($"{p.Id}: {string.Join("; ", bad)}");
                    }
                    catch (Exception ex) { failures.Add($"{p.Id}: threw {ex.GetType().Name}: {ex.Message}"); }
                    finally { try { vc?.Dispose(); } catch { } }
                }
            }
            foreach (var fail in failures) line("fixed-layout profile " + fail.Split(':')[0], false, fail);
            line("every Xbox and PlayStation profile's state", failures.Count == 0, $"{checkedCount} profiles checked, {failures.Count} wrong");
        }

        /// <summary>Where HIDMaestro's encoder puts each face button of a
        /// fixed-layout slot against where the profile's layout says that
        /// button is. The encoder places HMButton bit b at descriptor button
        /// ButtonMap[b], or b with no map (HidReportBuilder.cs:1165-1190), and
        /// the layout's faceButtons name each role's button. The two are
        /// HIDMaestro's own data, so a profile where they disagree is printed
        /// as a finding for HIDMaestro, apart from the probe's own checks, and
        /// the run with rights reads that family's A where the layout puts
        /// it.</summary>
        internal static List<string> FaceButtonFindings()
        {
            var findings = new List<string>();
            var roles = new[]
            {
                ("A", new[] { HMButtonRole.FaceA, HMButtonRole.FaceCross }),
                ("B", new[] { HMButtonRole.FaceB, HMButtonRole.FaceCircle }),
                ("X", new[] { HMButtonRole.FaceX, HMButtonRole.FaceSquare }),
                ("Y", new[] { HMButtonRole.FaceY, HMButtonRole.FaceTriangle }),
            };
            foreach (var list in new[] { HMaestroProfileCatalog.XboxProfiles, HMaestroProfileCatalog.PlayStationProfiles })
            {
                foreach (var p in list)
                {
                    var face = p.AsGamepad()?.FaceButtons;
                    if (face == null || face.Count == 0) continue;
                    var map = p.ButtonMap;
                    var wrong = new List<string>();
                    for (int bit = 0; bit < 4; bit++)
                    {
                        var binding = face.FirstOrDefault(f => f != null && Array.IndexOf(roles[bit].Item2, f.Role) >= 0);
                        if (binding == null) continue;
                        int encoded = map != null && bit < map.Length ? map[bit] : bit;
                        if (encoded != binding.ButtonIndex)
                            wrong.Add($"HMButton.{roles[bit].Item1} goes to descriptor button {encoded + 1}, the layout's {binding.Role} is button {binding.ButtonIndex + 1}");
                    }
                    if (wrong.Count > 0)
                        findings.Add($"{p.Id} ({(map == null ? "no buttonMap" : "buttonMap")}): {string.Join("; ", wrong)}");
                }
            }
            return findings;
        }

        private static void CheckKeyboard(Family f, Action<string, bool, string> line)
        {
            var picked = Families.PickKeyboard(out string why);
            line(f.Name + " F24 sendable", picked != null, picked != null ? $"VK 87 resolves to 07:73 and {picked.Id} carries usage 73" : why);
            if (picked == null) return;
            var sink = new KbmRecorder();
            var kbm = new KeyboardMouseVirtualController(Families.Pad, null, picked, null) { CreateSinkSeam = (_, _) => sink };
            try
            {
                kbm.Connect();
                var down = new KbmRawState { Keys2 = 1UL << (KnownState.VkF24 - 128) };
                kbm.SubmitKbmState(down, KbmCause.AllOwn);
                Thread.Sleep(100);
                kbm.SubmitKbmState(default, KbmCause.AllOwn);
                Thread.Sleep(200);
                List<string> keys; lock (sink.Log) keys = sink.Log.Where(l => l.StartsWith("keys", StringComparison.Ordinal)).ToList();
                string log = string.Join(" | ", keys);
                // The press, then the release: an empty key array.
                bool ok = keys.Count == 2 && keys[0] == "keys 73" && keys[1] == "keys ";
                line(f.Name + " reports handed to the keyboard", ok, log);
            }
            finally { kbm.Dispose(); }
        }

        private static void CheckMouse(Family f, Action<string, bool, string> line)
        {
            var picked = Families.PickMouse(out string why);
            line(f.Name + " mouse collection", picked != null, picked != null ? picked.Id : why);
            if (picked == null) return;
            var sink = new KbmRecorder();
            var kbm = new KeyboardMouseVirtualController(Families.Pad, null, null, picked) { CreateSinkSeam = (_, _) => sink };
            try
            {
                kbm.Connect();
                kbm.SubmitKbmState(new KbmRawState { MouseFlickX = 1 }, KbmCause.AllOwn);
                Thread.Sleep(150);
                kbm.SubmitKbmState(new KbmRawState { MouseFlickX = -1 }, KbmCause.AllOwn);
                Thread.Sleep(150);
                kbm.SubmitKbmState(default, KbmCause.AllOwn);
                Thread.Sleep(100);
                List<string> moves; lock (sink.Log) moves = sink.Log.Where(l => l.StartsWith("mouse ", StringComparison.Ordinal) && l != "mouse 0,0").ToList();
                bool ok = moves.Count == 2 && moves[0] == "mouse 1,0" && moves[1] == "mouse -1,0";
                line(f.Name + " reports handed to the mouse", ok, string.Join(" | ", moves));
            }
            finally { kbm.Dispose(); }
        }
    }
}
