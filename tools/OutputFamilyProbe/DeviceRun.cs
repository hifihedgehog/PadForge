using System.Diagnostics;
using HIDMaestro;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;

namespace PadForge.Tools.OutputFamilyProbe
{
    /// <summary>One family's line: created or the error, what was submitted,
    /// what Windows delivered, and the verdict.</summary>
    internal sealed class FamilyResult
    {
        public Family Family;
        public string Created = "no";
        public string Submitted = "nothing";
        public string Delivered = "nothing";
        public Verdict Verdict = new() { Failure = "not run" };

        public override string ToString()
            => $"{Family.Name} [{Family.ProfileId}] created: {Created} | submitted: {Submitted} | delivered: {Delivered} | {(Verdict.Pass ? "PASS" : "FAIL")} {(Verdict.Pass ? Verdict.Compared : Verdict.Failure + "; compared " + Verdict.Compared)}";
    }

    /// <summary>The run with rights: one family after another, each
    /// created, read and disposed before the next.</summary>
    internal sealed class DeviceRun
    {
        private readonly PadForgePath _path;
        private readonly RawInputReader _rawInput;
        private readonly CancellationToken _cancel;
        private readonly Action<string> _log;

        /// <summary>The controller alive now, which every exit path
        /// disposes.</summary>
        internal volatile IVirtualController Current;

        public DeviceRun(PadForgePath path, RawInputReader rawInput, CancellationToken cancel, Action<string> log)
        {
            _path = path;
            _rawInput = rawInput;
            _cancel = cancel;
            _log = log;
        }

        private static HashSet<uint> ConnectedXInput()
        {
            var set = new HashSet<uint>();
            for (uint i = 0; i < 4; i++) if (Native.XInputGetState(i, out _) == 0) set.Add(i);
            return set;
        }

        /// <summary>The poll loop's stand-in: one Pass every millisecond on a
        /// thread of its own, as InputManager's loop calls Step 5 at its
        /// default 1000 passes a second, neutral until <see cref="Known"/> is
        /// set.</summary>
        private sealed class Loop : IDisposable
        {
            private readonly Thread _thread;
            private volatile bool _stop;
            public volatile bool Known;
            public KbmRawState Kbm;
            /// <summary>Mouse counts for the next pass alone, taken by the
            /// loop under <see cref="KbmLock"/>.</summary>
            public int FlickOnce;
            public readonly object KbmLock = new();
            public long Passes;
            public string Error;

            public Loop(Submitter s)
            {
                _thread = new Thread(() =>
                {
                    var sw = Stopwatch.StartNew();
                    long next = 0;
                    while (!_stop)
                    {
                        try
                        {
                            KbmRawState k;
                            lock (KbmLock)
                            {
                                k = Kbm;
                                k.MouseFlickX = FlickOnce;
                                FlickOnce = 0;
                            }
                            s.Pass(!Known, k);
                            Interlocked.Increment(ref Passes);
                        }
                        catch (Exception ex) { Error ??= $"{ex.GetType().Name}: {ex.Message}"; }
                        next += 1;
                        long wait = next - sw.ElapsedMilliseconds;
                        if (wait > 0) Thread.Sleep((int)wait);
                    }
                }) { IsBackground = true, Name = "Probe poll loop" };
                _thread.Start();
            }

            public void Dispose() { _stop = true; _thread.Join(2000); }
        }

        public FamilyResult Run(Family f)
        {
            var r = new FamilyResult { Family = f };
            var beforeHid = HidInterfaces.Present();
            var beforeX = ConnectedXInput();
            int errorsBefore;
            lock (_path.Errors) errorsBefore = _path.Errors.Count;

            IVirtualController vc = null;
            try
            {
                vc = _path.Create(f);
            }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : ex;
                r.Created = $"error {inner.GetType().Name}: {inner.Message}";
                return r;
            }
            if (vc == null)
            {
                string err;
                lock (_path.Errors) err = string.Join(" / ", _path.Errors.Skip(errorsBefore));
                r.Created = "error " + (err.Length > 0 ? err : "CreateVirtualController returned null");
                return r;
            }
            Current = vc;
            r.Created = "yes";
            try
            {
                var submitter = new Submitter(f, vc, _path.Engine);
                switch (f.Read)
                {
                    case ReadKind.XInput: ReadXInput(f, vc, submitter, beforeX, r); break;
                    case ReadKind.Hid: ReadHid(f, vc, submitter, beforeHid, r); break;
                    case ReadKind.RawKeyboard: ReadKeyboard(f, submitter, beforeHid, r); break;
                    case ReadKind.RawMouse: ReadMouse(f, submitter, beforeHid, r); break;
                }
            }
            catch (Exception ex)
            {
                r.Verdict = new Verdict { Failure = $"the read threw {ex.GetType().Name}: {ex.Message}", Compared = "nothing" };
            }
            finally
            {
                Current = null;
                PadForgePath.Destroy(vc);
                // Every family of a type is made under one identity key, so the
                // next one comes back at the same paths. It is found by the
                // paths that are new after its create, so this one's paths must
                // be gone first: up to 30 s, and the wait is printed when they
                // outlast it.
                var mine = HidInterfaces.NewSince(beforeHid).Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var gone = Stopwatch.StartNew();
                while (mine.Count > 0 && gone.ElapsedMilliseconds < 30000)
                {
                    var now = HidInterfaces.Present().Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    mine.IntersectWith(now);
                    if (mine.Count > 0) Thread.Sleep(200);
                }
                if (mine.Count > 0) _log($"  ({f.Name}: {mine.Count} HID interface(s) still listed 30 s after the dispose)");
            }
            return r;
        }

        private void ReadXInput(Family f, IVirtualController vc, Submitter s, HashSet<uint> before, FamilyResult r)
        {
            using var loop = new Loop(s);
            // The user index the controller took: the first one that
            // connects after the create (HIDMaestro test/Latency.cs:92-104
            // takes the first connected index. A pad already plugged in is
            // left out here).
            int slot = -1;
            var find = Stopwatch.StartNew();
            while (find.ElapsedMilliseconds < 15000 && slot < 0 && !_cancel.IsCancellationRequested)
            {
                for (uint i = 0; i < 4; i++)
                    if (!before.Contains(i) && Native.XInputGetState(i, out _) == 0) { slot = (int)i; break; }
                if (slot < 0) Thread.Sleep(50);
            }
            if (slot < 0)
            {
                r.Submitted = "neutral";
                r.Verdict = new Verdict { Failure = "no new XInput user index connected within 15 s", Compared = "nothing" };
                return;
            }
            loop.Known = true;
            r.Submitted = "A, left stick full right and half up, right stick a quarter left, left trigger full";
            var deadline = Stopwatch.StartNew();
            Native.XINPUT_STATE last = default;
            while (deadline.ElapsedMilliseconds < 5000 && !_cancel.IsCancellationRequested)
            {
                if (Native.XInputGetState((uint)slot, out last) == 0)
                {
                    var v = Expect.XInput(in last);
                    if (v.Pass) { r.Verdict = v; break; }
                    r.Verdict = v;
                }
                Thread.Sleep(5);
            }
            var g = last.Gamepad;
            r.Delivered = $"XInput user {slot}: wButtons {g.wButtons:X4} LT {g.bLeftTrigger} RT {g.bRightTrigger} LX {g.sThumbLX} LY {g.sThumbLY} RX {g.sThumbRX} RY {g.sThumbRY}";
            if (loop.Error != null) r.Submitted += $" (submit threw {loop.Error})";
        }

        /// <summary>The virtual device's own HID interfaces: present now and
        /// not before the create, HIDMaestro's by PadForge's own test
        /// (HidHideController.IsHidMaestroDeviceInstance), and of the
        /// controller's vendor and product ids. A real pad of the same ids
        /// was present before and so is never one of them.</summary>
        private List<OpenHid> OpenOwnInterfaces(ushort vid, ushort pid, List<HidInterface> before, out string notes)
        {
            var open = new List<OpenHid>();
            var why = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var find = Stopwatch.StartNew();
            long settledAt = -1;
            while (find.ElapsedMilliseconds < 20000 && !_cancel.IsCancellationRequested)
            {
                foreach (var itf in HidInterfaces.NewSince(before))
                {
                    if (!seen.Add(itf.Path)) continue;
                    if (!HidHideController.IsHidMaestroDeviceInstance(itf.InstanceId))
                    {
                        why.Add($"{itf.InstanceId}: not HIDMaestro's");
                        continue;
                    }
                    var h = OpenHid.TryOpen(itf, out string refused);
                    if (h == null) { why.Add($"{itf.InstanceId}: {refused}"); continue; }
                    if (h.VendorId != vid || h.ProductId != pid)
                    {
                        why.Add($"{itf.InstanceId}: {h.VendorId:X4}:{h.ProductId:X4}");
                        h.Dispose();
                        continue;
                    }
                    open.Add(h);
                    if (settledAt < 0) settledAt = find.ElapsedMilliseconds;
                }
                // A device with several collections lists them together. A
                // second after the first, every one is there.
                if (settledAt >= 0 && find.ElapsedMilliseconds - settledAt > 1000) break;
                Thread.Sleep(100);
            }
            notes = why.Count == 0 ? string.Empty : "skipped " + string.Join("; ", why);
            return open;
        }

        private void ReadHid(Family f, IVirtualController vc, Submitter s, List<HidInterface> before, FamilyResult r)
        {
            var hm = vc as HMaestroVirtualController;
            var effective = hm != null ? PadForgePath.EffectiveProfile(hm) : f.Profile;
            HMButton expected = ExpectedButtons(f, effective);
            using var loop = new Loop(s);
            var open = OpenOwnInterfaces(effective.VendorId, effective.ProductId, before, out string notes);
            BulkStart bulk = null;
            try
            {
                if (open.Count == 0)
                {
                    r.Submitted = "neutral";
                    r.Verdict = new Verdict { Failure = $"no HID interface of the virtual device ({effective.VendorId:X4}:{effective.ProductId:X4}) opened within 20 s{(notes.Length > 0 ? ", " + notes : "")}", Compared = "nothing" };
                    return;
                }
                string armed = Arm(f, open) ?? StartOverBulk(f, open, out bulk);
                loop.Known = true;
                r.Submitted = KnownStateText(f) + (armed != null ? $"; {armed}" : "");
                // The newest report and its verdict, per interface. Reports
                // the poll loop's neutral passes made can still be queued, and
                // the known state is the newest, so a report is judged as it
                // arrives until one carries it.
                var last = new Dictionary<OpenHid, (byte[] Report, Verdict Verdict)>();
                var deadline = Stopwatch.StartNew();
                OpenHid passed = null;
                while (deadline.ElapsedMilliseconds < 6000 && passed == null && !_cancel.IsCancellationRequested)
                {
                    bool any = false;
                    foreach (var h in open)
                    {
                        var report = h.Read(10);
                        if (report == null) continue;
                        any = true;
                        var v = Evaluate(f, effective, h, report, expected);
                        last[h] = (report, v);
                        if (v.Pass) { passed = h; break; }
                    }
                    if (!any) Thread.Sleep(1);
                }
                if (last.Count == 0)
                {
                    r.Delivered = $"no input report in 6 s on {open.Count} interface(s) (caps {string.Join(", ", open.Select(h => $"{h.UsagePage:X2}:{h.Usage:X2} len {h.InputReportLength}"))})";
                    r.Verdict = new Verdict { Failure = "Windows delivered no report", Compared = "nothing" };
                    return;
                }
                // The interface that passed, else the one whose newest report
                // has the id the family's report carries, else the first that
                // reported. Its newest report is printed with its own verdict.
                byte? wantId = f.Route == Route.SInputPacked ? SInputReportPacker.ReportId : f.Spec?.ReportIdByte;
                var from = passed
                    ?? last.Keys.FirstOrDefault(h => wantId != null && last[h].Report.Length > 0 && last[h].Report[0] == wantId.Value)
                    ?? last.Keys.First();
                r.Delivered = $"{from.UsagePage:X2}:{from.Usage:X2} len {from.InputReportLength}: {Expect.Hex(last[from].Report)}";
                r.Verdict = last[from].Verdict;
                if (loop.Error != null) r.Submitted += $" (submit threw {loop.Error})";
            }
            finally
            {
                bulk?.Dispose();
                foreach (var h in open) h.Dispose();
            }
        }

        /// <summary>A pad that reports only once a host starts it over its
        /// bulk pair (<see cref="BulkStart"/>) gets that start, after a
        /// window that shows it silent before. The session stays open for
        /// the read. Returns what the start did, or null when the profile
        /// needs none.</summary>
        private static string StartOverBulk(Family f, List<OpenHid> open, out BulkStart bulk)
        {
            bulk = null;
            if (!BulkStart.Needs(f)) return null;
            int before = BulkStart.ReportsWithin(open, 300);
            string silent = $"{before} input report(s) in 300 ms before the start";
            bulk = BulkStart.Open(open[0], out string why);
            if (bulk == null) return $"{silent}; bulk start not sent: {why}";
            var reply = bulk.Start();
            if (reply == null) return $"{silent}; bulk start 03/0D: the write failed";
            bool expected = reply.AsSpan().SequenceEqual(BulkStart.StartReply);
            return $"{silent}; bulk start 03/0D answered {(reply.Length == 0 ? "nothing" : Expect.Hex(reply))}{(expected ? "" : $", want {Expect.Hex(BulkStart.StartReply)}")}";
        }

        /// <summary>A profile whose extended report waits on a host's
        /// feature read (HIDMaestro's armOn, the reads SDL makes as it opens a
        /// pad) gets that read, on every interface, before the reports are
        /// judged, each bounded at 2 s. Its earlier report is judged by the
        /// descriptor either way. Returns what the reads did, or null when the
        /// profile names none.</summary>
        private static string Arm(Family f, List<OpenHid> open)
        {
            var arm = f.Spec?.ArmOn;
            if (arm == null) return null;
            foreach (var t in arm)
            {
                if (!string.Equals(t.Type, "featureRead", StringComparison.Ordinal)) continue;
                var answers = new List<string>();
                foreach (var h in open)
                {
                    var read = Task.Run(() => h.GetFeature(t.ReportIdByte));
                    string answer = !read.Wait(2000) ? "no answer in 2 s" : read.Result ? "answered" : "refused";
                    answers.Add($"{h.UsagePage:X2}:{h.Usage:X2} {answer}");
                }
                return $"feature read {t.ReportId} to arm: {string.Join(", ", answers)}";
            }
            return null;
        }

        internal static HMButton ExpectedButtons(Family f, HMProfile effective)
        {
            if (f.Route != Route.RawState) return KnownState.GamepadButtons;
            // The raw lane hands HIDMaestro the mask HMaestroVirtualController's
            // BuildRawHidState builds, a raw index per bit or
            // the Switch 2 family's map. It needs no device.
            var probe = Families.Unconnected(effective, f.Type);
            return probe.BuildRawHidState(KnownState.Raw(f.ProfileId, f.Layout, false), f.Layout.Sticks, f.Layout.Triggers, default).Buttons;
        }

        internal static string KnownStateText(Family f) => f.Route switch
        {
            Route.Gamepad or Route.SonyPacked or Route.PlayStationState => "Gamepad A, LX +32767, LY +16384, RX -8192, RY 0, LT 65535, RT 0",
            _ => $"raw button {KnownState.AButtonIndex(f.ProfileId)} (A), LX +32767, LY -16384, RX -8192, RY 0{(f.Layout.Triggers > 0 ? ", LT full, RT rest" : "")}",
        };

        /// <summary>Judges one report by the comparison its family and report
        /// id call for.</summary>
        internal static Verdict Evaluate(Family f, HMProfile effective, OpenHid h, byte[] report, HMButton expected)
        {
            if (f.Route == Route.SInputPacked) return Expect.SInput(report);
            if (f.Type == VirtualControllerType.Extended && f.Customize)
                return Expect.ExtendedLayout(report, f.Layout, reportIdOne: HMaestroVirtualController.DescriptorHasPidFfbBlock(effective.DescriptorHex));
            if (f.Spec != null && report.Length > 0 && report[0] == f.Spec.ReportIdByte
                && (f.Spec.ReportIdByte != 0 || report.Length >= f.Spec.Size))
                return Expect.BySpec(f.Spec, report, expected,
                    f.Route == Route.RawState && f.Layout.Triggers == 0 ? 0 : KnownState.LeftTrigger,
                    stickYUp: BulkStart.Needs(f));
            if (string.Equals(f.ProfileId, "switch-pro", StringComparison.OrdinalIgnoreCase) && report.Length > 0 && report[0] == 0x30)
                return Expect.SwitchProFull(report, expected);
            if (string.Equals(f.ProfileId, Expect.SixaxisProfileId, StringComparison.OrdinalIgnoreCase) && report.Length > 0 && report[0] == 0x00)
                return Expect.SixaxisJoystick(report, expected);
            if (h == null) return new Verdict { Failure = "no comparison for this report without a device", Compared = "nothing" };
            return Expect.ByHidParser(h, report, effective, expected, byRole: f.Route is Route.Gamepad or Route.SonyPacked or Route.PlayStationState);
        }

        private List<string> NewInstanceIds(List<HidInterface> before)
        {
            var ids = new List<string>();
            var find = Stopwatch.StartNew();
            long settledAt = -1;
            while (find.ElapsedMilliseconds < 20000 && !_cancel.IsCancellationRequested)
            {
                foreach (var itf in HidInterfaces.NewSince(before))
                {
                    if (ids.Contains(itf.InstanceId, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!HidHideController.IsHidMaestroDeviceInstance(itf.InstanceId)) continue;
                    ids.Add(itf.InstanceId);
                    if (settledAt < 0) settledAt = find.ElapsedMilliseconds;
                }
                if (settledAt >= 0 && find.ElapsedMilliseconds - settledAt > 1000) break;
                Thread.Sleep(100);
            }
            return ids;
        }

        private void ReadKeyboard(Family f, Submitter s, List<HidInterface> before, FamilyResult r)
        {
            if (_rawInput.Error != null) { r.Verdict = new Verdict { Failure = "Raw Input: " + _rawInput.Error, Compared = "nothing" }; return; }
            using var loop = new Loop(s);
            var ids = NewInstanceIds(before);
            if (ids.Count == 0) { r.Verdict = new Verdict { Failure = "no HID interface of the virtual keyboard appeared within 20 s", Compared = "nothing" }; return; }
            Thread.Sleep(300);
            _rawInput.Clear();
            // F24 down for 100 ms, then up: Keys2 bit 7 is VK 0x87
            // (KbmRawState packs 256 keys into four words, GamepadTypes.cs).
            lock (loop.KbmLock) loop.Kbm.Keys2 = 1UL << (KnownState.VkF24 - 128);
            Thread.Sleep(100);
            lock (loop.KbmLock) loop.Kbm.Keys2 = 0;
            r.Submitted = "F24 down, then up 100 ms later";
            bool down = false, up = false;
            var seenOther = new List<string>();
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 3000 && !(down && up))
            {
                while (_rawInput.TryTake(out var e))
                {
                    if (!e.Keyboard) continue;
                    bool ours = ids.Contains(e.InstanceId, StringComparer.OrdinalIgnoreCase);
                    if (!ours) { seenOther.Add($"{e.InstanceId} vk {e.VKey:X2}"); continue; }
                    bool f24 = e.VKey == KnownState.VkF24 || e.MakeCode == KnownState.F24MakeCode;
                    if (f24 && !e.Break) down = true;
                    if (f24 && e.Break && down) up = true;
                }
                Thread.Sleep(5);
            }
            r.Delivered = $"Raw Input from {string.Join(", ", ids)}: F24 make {(down ? "seen" : "not seen")}, break {(up ? "seen" : "not seen")}"
                + (seenOther.Count > 0 ? $"; other devices: {string.Join(", ", seenOther.Take(4))}" : "");
            r.Verdict = new Verdict
            {
                Pass = down && up,
                Compared = "F24 make and break (VKey 87 or make code 76) from the virtual keyboard's handle",
                Failure = down ? "no F24 break" : "no F24 make",
            };
        }

        private void ReadMouse(Family f, Submitter s, List<HidInterface> before, FamilyResult r)
        {
            if (_rawInput.Error != null) { r.Verdict = new Verdict { Failure = "Raw Input: " + _rawInput.Error, Compared = "nothing" }; return; }
            using var loop = new Loop(s);
            var ids = NewInstanceIds(before);
            if (ids.Count == 0) { r.Verdict = new Verdict { Failure = "no HID interface of the virtual mouse appeared within 20 s", Compared = "nothing" }; return; }
            Thread.Sleep(300);
            _rawInput.Clear();
            // One count right, then one back: MouseFlickX goes to the mouse
            // 1:1 in counts (KeyboardMouseVirtualController.SubmitKbmState), the
            // one lane that is not a rate.
            // Each flick rides exactly one pass: the loop takes it and clears
            // it under one lock.
            void Flick(int x)
            {
                lock (loop.KbmLock) loop.FlickOnce = x;
                var wait = Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 1000)
                {
                    lock (loop.KbmLock) if (loop.FlickOnce == 0) break;
                    Thread.Sleep(1);
                }
            }
            Flick(1);
            Thread.Sleep(150);
            Flick(-1);
            r.Submitted = "MouseFlickX +1 for one pass, then -1 for one pass 150 ms later";
            var moves = new List<(int X, int Y)>();
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 3000)
            {
                while (_rawInput.TryTake(out var e))
                    if (!e.Keyboard && (e.X != 0 || e.Y != 0) && ids.Contains(e.InstanceId, StringComparer.OrdinalIgnoreCase)) moves.Add((e.X, e.Y));
                if (moves.Count >= 2) break;
                Thread.Sleep(5);
            }
            r.Delivered = $"Raw Input from {string.Join(", ", ids)}: {(moves.Count == 0 ? "no movement" : string.Join(" ", moves.Select(m => $"({m.X},{m.Y})")))}";
            bool pass = moves.Count == 2 && moves[0] == (1, 0) && moves[1] == (-1, 0);
            r.Verdict = new Verdict
            {
                Pass = pass,
                Compared = "relative movement (1,0) then (-1,0) from the virtual mouse's handle",
                Failure = "movement did not match",
            };
        }
    }
}
