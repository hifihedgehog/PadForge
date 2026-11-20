using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Flydigi;
using PadForge.Engine.Flydigi;
using PadForge.Services;
using Xunit;
using Proto = PadForge.Engine.Flydigi.FlydigiApexProtocol;
using X = PadForge.Engine.Flydigi.FlydigiApex4XinputScreen;

namespace PadForge.Tests
{
    /// <summary>
    /// A Flydigi Apex pad's session and screen send (#586) where they let a
    /// pad go: the crash path's stops, an unassigned pad, the grips'
    /// samples and an Apex 4's way back to DInput mode. In the peripheral
    /// collection, as the service and the session's opener are
    /// process-wide.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public sealed class FlydigiAuditFixTests : IDisposable
    {
        private readonly Func<string, IHidReportChannel> _opener = FlydigiApexSession.Opener;
        private readonly Action<Action> _post = FlydigiApexService.Post;
        private readonly Action<int, CancellationToken> _sleep = FlydigiScreenSender.Sleep;
        private readonly List<FlydigiApexSession> _sessions = new();

        public FlydigiAuditFixTests()
        {
            FlydigiApexService.Restart();
            FlydigiApexService.Post = work => work();
            FlydigiScreenSender.Sleep = (_, _) => { };
            FlydigiApexService.ForgetAllForTest();
        }

        public void Dispose()
        {
            foreach (var session in _sessions) session.Dispose();
            FlydigiApexSession.Opener = _opener;
            FlydigiApexService.Post = _post;
            FlydigiScreenSender.Sleep = _sleep;
            FlydigiApexService.ForgetAllForTest();
        }

        private FlydigiApexSession Open(FlydigiApexRuntimeTests.ScriptedPad pad, string path, FlydigiApexModel model)
        {
            FlydigiApexSession.Opener = p => pad.Open(p);
            var session = FlydigiApexSession.Open(path, model);
            Assert.NotNull(session);
            _sessions.Add(session);
            return session;
        }

        private static byte[] Apex5Reply(byte command, params (int At, byte Value)[] bytes)
        {
            var reply = new byte[32];
            reply[0] = 0x04; reply[1] = 0x5A; reply[2] = 0xA5; reply[3] = command;
            foreach (var (at, value) in bytes) reply[at] = value;
            return reply;
        }

        private static FlydigiApexRuntimeTests.ScriptedPad Apex5Pad()
        {
            var pad = new FlydigiApexRuntimeTests.ScriptedPad { InputLength = 32, OutputLength = 32 };
            pad.Respond = report => report[0] == 0x03 && report[3] == 0x01
                ? new[] { Apex5Reply(0x01, (6, 128), (7, 1)) }
                : Array.Empty<byte[]>();
            return pad;
        }

        private static FlydigiApexRuntimeTests.ScriptedPad Apex4Pad()
        {
            var reply = new byte[32];
            reply[15] = 0xEC; reply[3] = 84; reply[13] = 1;
            var pad = new FlydigiApexRuntimeTests.ScriptedPad { InputLength = 32, OutputLength = 15 };
            pad.Respond = report => report[0] == 0x05 && report[1] == 0xEC ? new[] { reply } : Array.Empty<byte[]>();
            return pad;
        }

        private static byte[] Apex6Reply(byte command, byte mode, Action<byte[]> fill)
        {
            var report = new byte[33];
            report[1] = 0x5A; report[2] = 0xA5; report[3] = command; report[4] = mode;
            fill?.Invoke(report);
            report[32] = Proto.Apex6Checksum(report.AsSpan(1));
            return report;
        }

        private static FlydigiApexRuntimeTests.ScriptedPad Apex6Pad()
        {
            var pad = new FlydigiApexRuntimeTests.ScriptedPad { InputLength = 33, OutputLength = 33 };
            pad.Respond = report => report.Length > 3 && report[3] == 0x01
                ? new[] { Apex6Reply(0x01, 1, r => { r[6] = 0x96; r[30] = 0x98; }) }
                : Array.Empty<byte[]>();
            return pad;
        }

        private static bool Is(byte[] write, byte[] command) => write.AsSpan().SequenceEqual(command);

        /// <summary>The crash path hands an Apex 6 Pro's motors back to the
        /// pad, and the session's own stream thread must not put them back
        /// on the stream after it. Quiet left the grips wanted for haptic
        /// samples, so the thread found work and turned the stream on
        /// again, with nothing to feed it once the process died.</summary>
        [Fact]
        public void AnApex6ProsMotorsStayHandedBack_AfterTheCrashPathsStop()
        {
            var pad = Apex6Pad();
            var session = Open(pad, @"\\?\hid#vid_37d7&pid_2502#audit-a", FlydigiApexModel.Apex6Pro);
            session.SetGripPcmWanted(true);
            Assert.True(SpinWait.SpinUntil(() => session.Streaming, 5000), "the stream did not start");
            session.Quiet();
            session.QuietNow();
            Thread.Sleep(500);
            var writes = pad.WritesOf(_ => true);
            int off = writes.FindLastIndex(w => Is(w, Proto.Apex6GripStream(false)));
            Assert.True(off >= 0, "the grips were not handed back");
            Assert.DoesNotContain(writes.Skip(off + 1), w => Is(w, Proto.Apex6GripStream(true)));
            Assert.False(session.Streaming);
        }

        /// <summary>The crash path releases an Apex 4's triggers three
        /// times, as flydigi-apex4-linux does on exit (apex4ds5/triggers.py
        /// reset: one release of each side per entry of SEND_SCHEDULE_S), so
        /// a lost packet cannot leave a trigger effect on. It sent two.</summary>
        [Fact]
        public void TheCrashPathReleasesAnApex4sTriggersThreeTimes()
        {
            var pad = Apex4Pad();
            var session = Open(pad, @"\\?\hid#vid_04b4&pid_2412&mi_02#audit-b", FlydigiApexModel.Apex4);
            lock (pad.Gate) pad.Writes.Clear();
            session.QuietNow();
            byte[] left = Proto.Apex4Trigger(FlydigiTriggerCommand.Off(FlydigiTriggerSide.Left));
            byte[] right = Proto.Apex4Trigger(FlydigiTriggerCommand.Off(FlydigiTriggerSide.Right));
            Assert.Equal(3, pad.WritesOf(w => Is(w, left)).Count);
            Assert.Equal(3, pad.WritesOf(w => Is(w, right)).Count);
        }

        /// <summary>A pad with no slot is let go once its session is up. The
        /// let-go was spent on the first pass, while the session was still
        /// asking the pad what it is, and the session that came up seconds
        /// later kept whatever effect the pad held.</summary>
        [Fact]
        public void AnUnassignedPadIsLetGo_WhenItsSessionComesUp()
        {
            var device = Guid.NewGuid();
            string path = @"\\?\hid#vid_37d7&pid_2501#audit-c";
            FlydigiApexService.AddForTest(device, path, null);
            FlydigiApexService.Unassigned(device);

            var pad = Apex5Pad();
            var session = Open(pad, path, FlydigiApexModel.Apex5);
            FlydigiApexService.AddForTest(device, path, session);
            lock (pad.Gate) pad.Writes.Clear();
            FlydigiApexService.Unassigned(device);
            byte[] left = Proto.Apex5Trigger(FlydigiTriggerCommand.Off(FlydigiTriggerSide.Left));
            Assert.True(SpinWait.SpinUntil(() => pad.WritesOf(w => Is(w, left)).Count > 0, 3000),
                "the unassigned pad's triggers were not released");
        }

        /// <summary>An Apex 6 Pro's grips take a block of samples every
        /// 8 ms per pair, and each block reuses a buffer the stream is done
        /// with. Each made two arrays.</summary>
        [Fact]
        public void TheGripsSamplesAllocateNothing()
        {
            var pad = Apex6Pad();
            var session = Open(pad, @"\\?\hid#vid_37d7&pid_2502#audit-d", FlydigiApexModel.Apex6Pro);
            var left = new sbyte[Proto.Apex6SamplesPerReport];
            var right = new sbyte[Proto.Apex6SamplesPerReport];
            left[0] = 40; right[0] = -40;
            Assert.All(AllocationWindow.Clean(() => session.SubmitGripPcm(left, right), 50), bytes => Assert.Equal(0, bytes));
        }

        /// <summary>An Apex 4 whose send stops after it answered as an Apex 4
        /// (canceled, or a packet with no answer) is switched back to DInput
        /// mode before its pipe closes, as the sender's summary promises. It
        /// was switched back only after an upload that finished, and a
        /// canceled send left it in Xbox mode, out of PadForge's
        /// reach.</summary>
        [Fact]
        public void AnApex4SendThatStopsMidway_SwitchesThePadBack()
        {
            var nodes = FlydigiScreenSender.UsbNodes;
            var name = FlydigiScreenSender.ProductStringOf;
            var bind = FlydigiScreenSender.BindWinUsb;
            var restore = FlydigiScreenSender.RestoreDriver;
            var open = FlydigiScreenSender.OpenXbox;
            try
            {
                var pad = Apex4Pad();
                var session = Open(pad, @"\\?\hid#vid_04b4&pid_2412&mi_02#audit-e", FlydigiApexModel.Apex4);
                bool switched = false;
                var node = new VendorUsbDriverInstaller.UsbNode(@"USB\VID_045E&PID_028E\9&9", 0x045E, 0x028E, -1, "xusb22",
                    Array.Empty<string>(), Array.Empty<string>());
                pad.Respond = report =>
                {
                    if (report[0] == 0x05 && report[1] == X.DInputSwitchToXInput) switched = true;
                    return Array.Empty<byte[]>();
                };
                FlydigiScreenSender.UsbNodes = () => switched ? new[] { node } : Array.Empty<VendorUsbDriverInstaller.UsbNode>();
                FlydigiScreenSender.ProductStringOf = _ => "Flydigi APEX4";
                FlydigiScreenSender.BindWinUsb = (_, _, _, _) => true;
                FlydigiScreenSender.RestoreDriver = (_, _) => true;
                using var cancel = new CancellationTokenSource();
                var pipe = new CancelingXboxPad(cancel);
                FlydigiScreenSender.OpenXbox = _ => pipe;
                var ex = Assert.Throws<FlydigiScreenException>(() =>
                    FlydigiScreenSender.Send(session, new FlydigiScreenPicture(new[] { FlydigiScreenFormat.EncodeFrame(new byte[160 * 80 * 3]) }, 100),
                        new FlydigiScreenSender.Apex4Binding(), null, cancel.Token));
                Assert.Equal(FlydigiScreenFailure.Canceled, ex.Failure);
                Assert.Equal(X.CmdSwitchToDInput, pipe.Packets[^1][1]);
            }
            finally
            {
                FlydigiScreenSender.UsbNodes = nodes;
                FlydigiScreenSender.ProductStringOf = name;
                FlydigiScreenSender.BindWinUsb = bind;
                FlydigiScreenSender.RestoreDriver = restore;
                FlydigiScreenSender.OpenXbox = open;
            }
        }

        /// <summary>An Apex 4 whose switch back to DInput mode is refused
        /// after the picture went out ends with a line that says so. The
        /// write's answer was dropped, and the send reported it sent while
        /// the pad stayed in Xbox mode.</summary>
        [Fact]
        public void AnApex4ThatDoesNotSwitchBack_IsReported()
        {
            var nodes = FlydigiScreenSender.UsbNodes;
            var name = FlydigiScreenSender.ProductStringOf;
            var bind = FlydigiScreenSender.BindWinUsb;
            var restore = FlydigiScreenSender.RestoreDriver;
            var open = FlydigiScreenSender.OpenXbox;
            try
            {
                var pad = Apex4Pad();
                var session = Open(pad, @"\\?\hid#vid_04b4&pid_2412&mi_02#audit-f", FlydigiApexModel.Apex4);
                bool switched = false;
                var node = new VendorUsbDriverInstaller.UsbNode(@"USB\VID_045E&PID_028E\9&8", 0x045E, 0x028E, -1, "xusb22",
                    Array.Empty<string>(), Array.Empty<string>());
                pad.Respond = report =>
                {
                    if (report[0] == 0x05 && report[1] == X.DInputSwitchToXInput) switched = true;
                    return Array.Empty<byte[]>();
                };
                FlydigiScreenSender.UsbNodes = () => switched ? new[] { node } : Array.Empty<VendorUsbDriverInstaller.UsbNode>();
                FlydigiScreenSender.ProductStringOf = _ => "Flydigi APEX4";
                FlydigiScreenSender.BindWinUsb = (_, _, _, _) => true;
                FlydigiScreenSender.RestoreDriver = (_, _) => true;
                var pipe = new CancelingXboxPad(null) { RefuseSwitchBack = true };
                FlydigiScreenSender.OpenXbox = _ => pipe;
                var ex = Assert.Throws<FlydigiScreenException>(() =>
                    FlydigiScreenSender.Send(session, new FlydigiScreenPicture(new[] { FlydigiScreenFormat.EncodeFrame(new byte[160 * 80 * 3]) }, 100),
                        new FlydigiScreenSender.Apex4Binding(), null, CancellationToken.None));
                Assert.Equal(FlydigiScreenFailure.NotSwitchedBack, ex.Failure);
            }
            finally
            {
                FlydigiScreenSender.UsbNodes = nodes;
                FlydigiScreenSender.ProductStringOf = name;
                FlydigiScreenSender.BindWinUsb = bind;
                FlydigiScreenSender.RestoreDriver = restore;
                FlydigiScreenSender.OpenXbox = open;
            }
        }

        /// <summary>A send to a session that closed stops at once with a
        /// line that says the pad did not take the command. The command's
        /// refusal was dropped, and the send waited ten seconds for an Xbox
        /// pad that never came, then said to switch back a pad that never
        /// left DInput mode.</summary>
        [Fact]
        public void ASendToAClosedSession_StopsAtOnce()
        {
            var nodes = FlydigiScreenSender.UsbNodes;
            try
            {
                FlydigiScreenSender.UsbNodes = () => Array.Empty<VendorUsbDriverInstaller.UsbNode>();
                var pad = Apex4Pad();
                var session = Open(pad, @"\\?\hid#vid_04b4&pid_2412&mi_02#audit-g", FlydigiApexModel.Apex4);
                session.Dispose();
                var ex = Assert.Throws<FlydigiScreenException>(() =>
                    FlydigiScreenSender.Send(session, new FlydigiScreenPicture(new[] { FlydigiScreenFormat.EncodeFrame(new byte[160 * 80 * 3]) }, 100),
                        new FlydigiScreenSender.Apex4Binding(), null, CancellationToken.None));
                Assert.Equal(FlydigiScreenFailure.SwitchNotSent, ex.Failure);
            }
            finally
            {
                FlydigiScreenSender.UsbNodes = nodes;
            }
        }

        /// <summary>Each failure has its own line. A port that did not open
        /// names Flydigi Space Station only while it runs, a picture with too
        /// many frames, a send already running and an error in PadForge no
        /// longer read as the screen refusing the picture or no longer
        /// answering.</summary>
        [Fact]
        public void EachScreenFailureHasItsOwnLine()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var running = typeof(FlydigiServiceWatch).GetField("_running",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            object before = running.GetValue(null);
            try
            {
                running.SetValue(null, string.Empty);
                Assert.Equal(s.FlydigiScreen_Fail_PortDidNotOpenPlain, PadForge.Views.FlydigiScreenDialog.FailureText(FlydigiScreenFailure.PortDidNotOpen));
                running.SetValue(null, "SpaceStationService");
                Assert.Equal(s.FlydigiScreen_Fail_PortDidNotOpen, PadForge.Views.FlydigiScreenDialog.FailureText(FlydigiScreenFailure.PortDidNotOpen));
            }
            finally
            {
                running.SetValue(null, before);
            }
            var lines = Enum.GetValues<FlydigiScreenFailure>().Where(f => f is not FlydigiScreenFailure.None and not FlydigiScreenFailure.PortDidNotOpen)
                .Select(PadForge.Views.FlydigiScreenDialog.FailureText).ToList();
            Assert.Equal(lines.Count, lines.Distinct().Count());
            Assert.Equal(s.FlydigiScreen_Fail_TooManyFrames, PadForge.Views.FlydigiScreenDialog.FailureText(FlydigiScreenFailure.TooManyFrames));
            Assert.Equal(s.FlydigiScreen_Fail_Fault, PadForge.Views.FlydigiScreenDialog.FailureText(FlydigiScreenFailure.Fault));
        }

        /// <summary>The screen dialog's Send follows the pad's session, and a
        /// Send takes the session the pad has then. The dialog kept the one
        /// it opened with, which a finished send closes as the pad leaves
        /// and comes back.</summary>
        [Fact]
        public void TheScreenDialogsSendFollowsThePadsSession()
        {
            var device = Guid.NewGuid();
            var picture = new FlydigiScreenPicture(new[] { FlydigiScreenFormat.EncodeFrame(new byte[160 * 80 * 3]) }, 100);
            Assert.False(PadForge.Views.FlydigiScreenDialog.CanSend(true, picture, device));
            var pad = Apex5Pad();
            string path = @"\\?\hid#vid_37d7&pid_2501#audit-h";
            var session = Open(pad, path, FlydigiApexModel.Apex5);
            FlydigiApexService.AddForTest(device, path, session);
            Assert.True(PadForge.Views.FlydigiScreenDialog.CanSend(true, picture, device));
            Assert.False(PadForge.Views.FlydigiScreenDialog.CanSend(false, picture, device));
            FlydigiApexService.ForgetAllForTest();
            Assert.False(PadForge.Views.FlydigiScreenDialog.CanSend(true, picture, device));

            string dialog = ExtrasPageTests.RepoText("PadForge.App", "Views", "FlydigiScreenDialog.xaml.cs");
            Assert.Contains("var pad = FlydigiApexService.SessionOf(_device);", dialog);
            Assert.Contains("await FlydigiScreenSender.SendAsync(pad, picture, _binding, progress, _sending.Token);", dialog);
            Assert.Contains("FlydigiApexService.Changed += changed;", dialog);
        }

        /// <summary>The dispatcher's and the service's other let-gos: a pad
        /// that leaves a slot leaves its trigger state, a disposed slot
        /// leaves its Flydigi mark, a session's arrival runs every slot's
        /// full pass, the grips are armed again on a session that replaced
        /// the armed one, and the engine's stop closes the sessions off its
        /// own thread within a bound.</summary>
        [Fact]
        public void TheOtherLetGosAreWired()
        {
            string dispatcher = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "UserEffectsDispatcher.cs");
            Assert.Contains("_flydigiSides.Remove(g);", dispatcher);
            Assert.Contains("if (registered) NoteFlydigiSlot(false, false);", dispatcher);
            string service = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("FlydigiApexService.Changed += OnFlydigiSessionsChanged;", service);
            Assert.Contains("UserEffectsDispatcher.ApplyOnceAll();", service.Substring(service.IndexOf("private void OnFlydigiSessionsChanged()", StringComparison.Ordinal)));
            string tone = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "HapticToneService.cs");
            Assert.Contains("if (want != s.PcmArmed || (want && !ReferenceEquals(s.PcmArmedFor, pad)))", tone);
            string apex = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "Flydigi", "FlydigiApexService.cs");
            Assert.Contains("System.Threading.Tasks.Task.WaitAll(closing.ToArray(), ShutdownWaitMs);", apex);
        }

        /// <summary>An Apex 4 in Xbox mode that answers the device-info
        /// command and the screen's start, and cancels the send at the
        /// first data packet, as a user who closes the dialog does.</summary>
        private sealed class CancelingXboxPad : FlydigiScreenSender.IXboxPipe
        {
            public readonly List<byte[]> Packets = new();
            private readonly Queue<byte[]> _reports = new();
            private readonly CancellationTokenSource _cancel;
            public bool RefuseSwitchBack;

            public CancelingXboxPad(CancellationTokenSource cancel) => _cancel = cancel;

            public bool Write(byte[] packet)
            {
                Packets.Add((byte[])packet.Clone());
                var report = new byte[64];
                if (packet[1] == X.CmdDeviceInfo)
                {
                    report[14] = 0xA5; report[15] = 0x10; report[16] = 84; report[25] = 1;
                }
                else if (packet[1] == X.CmdSwitchToDInput)
                {
                    return !RefuseSwitchBack;
                }
                else if (packet[1] == X.CmdScreenData && _cancel != null)
                {
                    _cancel.Cancel();
                    return true;
                }
                else if (packet[1] >= X.CmdScreenStart && packet[1] <= X.CmdScreenEndAll)
                {
                    report[14] = 0x5A; report[15] = 0xA5; report[16] = packet[1];
                }
                else return true;
                _reports.Enqueue(report);
                return true;
            }

            public int Read(byte[] buffer, int timeoutMs)
            {
                if (_reports.Count == 0) return 0;
                var report = _reports.Dequeue();
                report.CopyTo(buffer, 0);
                return report.Length;
            }

            public void Dispose() { }
        }
    }
}
