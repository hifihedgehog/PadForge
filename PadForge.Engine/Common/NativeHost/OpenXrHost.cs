using System;
using System.Collections.Generic;
using PadForge.Engine.Common;
using PadForge.Engine.Common.OpenXr;
using static PadForge.Engine.Common.OpenXr.OpenXrInterop;

namespace PadForge.Engine.Common.NativeHost
{
    /// <summary>The OpenXR link's calls over the host's channel (#600), one
    /// per <see cref="IOpenXrLink"/> member. Every answer ends with the
    /// link's properties and the lines the runtime's code logged during the
    /// call.</summary>
    internal static class OpenXrOps
    {
        internal const int Create = 1, TryCreateSession = 2, PumpEvents = 3, DestroySession = 4, ReadHands = 5,
            TryLocateHead = 6, Dispose = 7;

        /// <summary>The bounds PadForge holds the host's answers to:
        /// positions in meters, angles in degrees, inputs in their unit
        /// range with room for a runtime's overshoot, which PadForge clamps.</summary>
        internal const double MaxMeters = 1000, MaxDegrees = 720;
        internal const float MaxInput = 1.5f, MaxQuaternion = 2f;
        internal const int MaxLogLines = 16, MaxLine = 1024, MaxName = 256, MaxPath = 1024;
        internal const long MaxWaitMs = 300_000;
    }

    /// <summary>
    /// The host's side of OpenXR: the runtime's library, which a manifest
    /// in the runtime's own folder names, loaded and negotiated with in the
    /// host by the link PadForge used in process
    /// (<see cref="OpenXrSession.TryCreate(string, bool, Action{string}, out OpenXrSourceState, out bool, out string)"/>),
    /// one link per channel.
    /// </summary>
    internal sealed class OpenXrHostService : IHostService
    {
        internal const string Name = "openxr";

        /// <summary>Test seam: the opener in place of the runtime's. Null in
        /// production.</summary>
        internal static OpenXrHeadPoseSource.LinkOpener OpenSeam;

        private readonly List<string> _log = new();

        public void Serve(HostConnection channel)
        {
            IOpenXrLink link = null;
            void Log(string line)
            {
                lock (_log)
                    if (_log.Count < OpenXrOps.MaxLogLines) _log.Add(line);
            }
            try
            {
                while (channel.Receive(out var r))
                {
                    int op = r.Int();
                    bool sent;
                    switch (op)
                    {
                        case OpenXrOps.Create:
                        {
                            string manifest = r.Text(OpenXrOps.MaxPath);
                            bool again = r.Bool();
                            r.End();
                            link?.Dispose();
                            link = null;
                            var open = OpenSeam ?? OpenXrSession.TryCreate;
                            link = open(manifest, again, Log, out var failure, out bool unavailable, out string read);
                            var made = link;
                            sent = channel.Send(w => Tail(w.Bool(made != null).Int((int)failure).Bool(unavailable).Text(Cut(read)), made));
                            break;
                        }
                        case OpenXrOps.TryCreateSession:
                        {
                            r.End();
                            var failure = OpenXrSourceState.Failed;
                            string read = "no instance";
                            var made = link?.TryCreateSession(out failure, out read) ?? OpenXrSessionCreate.Failed;
                            sent = channel.Send(w => Tail(w.Int((int)made).Int((int)failure).Text(Cut(read)), link));
                            break;
                        }
                        case OpenXrOps.PumpEvents:
                        {
                            r.End();
                            bool changed = false;
                            string read = null;
                            var end = link?.PumpEvents(out changed, out read) ?? OpenXrSessionEnd.InstanceLost;
                            sent = channel.Send(w => Tail(w.Int((int)end).Bool(changed).Text(Cut(read)), link));
                            break;
                        }
                        case OpenXrOps.DestroySession:
                            r.End();
                            link?.DestroySession();
                            sent = channel.Send(w => Tail(w, link));
                            break;
                        case OpenXrOps.ReadHands:
                        {
                            var left = ReadBaseline(r);
                            var right = ReadBaseline(r);
                            r.End();
                            OpenXrHandState leftState = OpenXrHandState.Empty, rightState = OpenXrHandState.Empty;
                            link?.ReadHands(ref left, ref right, out leftState, out rightState);
                            sent = channel.Send(w =>
                            {
                                WriteBaseline(w, left);
                                WriteBaseline(w, right);
                                WriteHand(w, leftState);
                                WriteHand(w, rightState);
                                Tail(w, link);
                            });
                            break;
                        }
                        case OpenXrOps.TryLocateHead:
                        {
                            r.End();
                            XrVector3f p = default;
                            XrQuaternionf q = default;
                            bool ok = link != null && link.TryLocateHead(out p, out q);
                            sent = channel.Send(w => Tail(w.Bool(ok).Float(p.x).Float(p.y).Float(p.z).Float(q.x).Float(q.y).Float(q.z).Float(q.w), link));
                            break;
                        }
                        case OpenXrOps.Dispose:
                            r.End();
                            link?.Dispose();
                            link = null;
                            sent = channel.Send(w => Tail(w, null));
                            break;
                        default:
                            throw new HostProtocolException("unknown OpenXR call");
                    }
                    if (!sent) return;
                }
            }
            finally
            {
                link?.Dispose();
            }
        }

        private static string Cut(string text) => text == null || text.Length <= OpenXrOps.MaxLine ? text : text.Substring(0, OpenXrOps.MaxLine);

        /// <summary>The link's properties and the call's log lines.</summary>
        private void Tail(MessageWriter w, IOpenXrLink link)
        {
            w.Text(Cut(link?.RuntimeName ?? string.Empty)).Text(Cut(link?.ManifestPath ?? string.Empty))
             .Bool(link?.HasSession ?? false).Bool(link?.HasControllers ?? false).Bool(link?.SharesOpenVrClient ?? false)
             .Long(Math.Clamp(link?.InstanceLossWaitMs ?? 0, 0, OpenXrOps.MaxWaitMs));
            lock (_log)
            {
                w.Int(_log.Count);
                foreach (string line in _log) w.Text(Cut(line));
                _log.Clear();
            }
        }

        internal static OpenXrHeadPose.Baseline ReadBaseline(MessageReader r) => new()
        {
            Captured = r.Bool(),
            X = r.Double(-OpenXrOps.MaxMeters, OpenXrOps.MaxMeters),
            Y = r.Double(-OpenXrOps.MaxMeters, OpenXrOps.MaxMeters),
            Z = r.Double(-OpenXrOps.MaxMeters, OpenXrOps.MaxMeters),
            YawDeg = r.Double(-OpenXrOps.MaxDegrees, OpenXrOps.MaxDegrees),
        };

        internal static void WriteBaseline(MessageWriter w, OpenXrHeadPose.Baseline b)
            => w.Bool(b.Captured).Double(b.X).Double(b.Y).Double(b.Z).Double(b.YawDeg);

        internal static void WriteHand(MessageWriter w, OpenXrHandState h)
            => w.Bool(h.PoseValid).Double(h.TX).Double(h.TY).Double(h.TZ).Double(h.YawDeg).Double(h.PitchDeg).Double(h.RollDeg)
                .Bool(h.ControlsActive).Float(h.Trigger).Float(h.Squeeze).Float(h.ThumbstickX).Float(h.ThumbstickY)
                .Bool(h.ThumbstickClick).Bool(h.PrimaryButton).Bool(h.SecondaryButton).Bool(h.MenuButton);

        /// <summary>A hand's state from the host, each number in its bound
        /// and each input clamped to its range.</summary>
        internal static OpenXrHandState ReadHand(MessageReader r)
        {
            double M() => r.Double(-OpenXrOps.MaxMeters, OpenXrOps.MaxMeters);
            double D() => r.Double(-OpenXrOps.MaxDegrees, OpenXrOps.MaxDegrees);
            float I() => r.Float(-OpenXrOps.MaxInput, OpenXrOps.MaxInput);
            return new OpenXrHandState
            {
                PoseValid = r.Bool(),
                TX = M(), TY = M(), TZ = M(),
                YawDeg = D(), PitchDeg = D(), RollDeg = D(),
                ControlsActive = r.Bool(),
                Trigger = Math.Clamp(I(), 0f, 1f),
                Squeeze = Math.Clamp(I(), 0f, 1f),
                ThumbstickX = Math.Clamp(I(), -1f, 1f),
                ThumbstickY = Math.Clamp(I(), -1f, 1f),
                ThumbstickClick = r.Bool(),
                PrimaryButton = r.Bool(),
                SecondaryButton = r.Bool(),
                MenuButton = r.Bool(),
            };
        }
    }

    /// <summary>
    /// PadForge's side of OpenXR: <see cref="IOpenXrLink"/> over the host,
    /// for <see cref="OpenXrHeadPoseSource"/>'s sampling thread. A host that
    /// ended reads as a lost instance with <see cref="HostEnded"/> set, which
    /// the source answers by retiring the link and asking for a new one,
    /// except on a runtime that is an OpenVR client library.
    /// </summary>
    internal sealed class RemoteOpenXrLink : IOpenXrLink
    {
        private readonly HostConnection _channel;
        private readonly Action<string> _log;

        private RemoteOpenXrLink(HostConnection channel, Action<string> log)
        {
            _channel = channel;
            _log = log ?? (_ => { });
        }

        public string RuntimeName { get; private set; } = string.Empty;
        public string ManifestPath { get; private set; } = string.Empty;
        public bool HasSession { get; private set; }
        public bool HasControllers { get; private set; }
        public bool SharesOpenVrClient { get; private set; }
        public long InstanceLossWaitMs { get; private set; }
        public bool HostEnded => _channel.Broken;

        /// <summary>The source's opener: a channel to the host, and the
        /// runtime negotiated with there.</summary>
        internal static OpenXrHeadPoseSource.LinkOpener Opener(HostOpener open)
            => (string manifestPath, bool again, Action<string> log, out OpenXrSourceState failure, out bool runtimeUnavailable, out string read) =>
            {
                failure = OpenXrSourceState.Failed;
                runtimeUnavailable = false;
                read = null;
                var channel = open(OpenXrHostService.Name, out string reason);
                if (channel == null)
                {
                    read = reason;
                    return null;
                }
                var link = new RemoteOpenXrLink(channel, log);
                bool created = false;
                var f = OpenXrSourceState.Failed;
                bool unavailable = false;
                string text = null;
                if (!channel.Call(w => w.Int(OpenXrOps.Create).Text(manifestPath).Bool(again), r =>
                    {
                        created = r.Bool();
                        f = (OpenXrSourceState)r.Int(0, (int)OpenXrSourceState.Ended);
                        unavailable = r.Bool();
                        text = r.Text(OpenXrOps.MaxLine);
                        link.ReadTail(r);
                    }))
                {
                    read = "the library host ended";
                    return null;
                }
                failure = f;
                runtimeUnavailable = unavailable;
                read = ShownText.Clean(text);
                if (created) return link;
                channel.Dispose();
                return null;
            };

        private void ReadTail(MessageReader r)
        {
            RuntimeName = ShownText.Clean(r.Text(OpenXrOps.MaxLine)) ?? string.Empty;
            ManifestPath = ShownText.Clean(r.Text(OpenXrOps.MaxLine)) ?? string.Empty;
            HasSession = r.Bool();
            HasControllers = r.Bool();
            SharesOpenVrClient = r.Bool();
            InstanceLossWaitMs = r.Long();
            if (InstanceLossWaitMs < 0 || InstanceLossWaitMs > OpenXrOps.MaxWaitMs) throw new HostProtocolException("wait out of bounds");
            int lines = r.Int(0, OpenXrOps.MaxLogLines);
            for (int i = 0; i < lines; i++) _log(ShownText.Clean(r.Text(OpenXrOps.MaxLine)) ?? string.Empty);
        }

        public OpenXrSessionCreate TryCreateSession(out OpenXrSourceState failure, out string read)
        {
            var made = OpenXrSessionCreate.Failed;
            var f = OpenXrSourceState.Failed;
            string text = null;
            if (!_channel.Call(w => w.Int(OpenXrOps.TryCreateSession), r =>
                {
                    made = (OpenXrSessionCreate)r.Int(0, (int)OpenXrSessionCreate.SessionLost);
                    f = (OpenXrSourceState)r.Int(0, (int)OpenXrSourceState.Ended);
                    text = r.Text(OpenXrOps.MaxLine);
                    ReadTail(r);
                }))
            {
                failure = OpenXrSourceState.Failed;
                read = "the library host ended";
                HasSession = false;
                return OpenXrSessionCreate.Failed;
            }
            failure = f;
            read = ShownText.Clean(text);
            return made;
        }

        public OpenXrSessionEnd PumpEvents(out bool referenceSpaceChanged, out string read)
        {
            var end = OpenXrSessionEnd.InstanceLost;
            bool changed = false;
            string text = null;
            if (!_channel.Call(w => w.Int(OpenXrOps.PumpEvents), r =>
                {
                    end = (OpenXrSessionEnd)r.Int(0, (int)OpenXrSessionEnd.BeginRefused);
                    changed = r.Bool();
                    text = r.Text(OpenXrOps.MaxLine);
                    ReadTail(r);
                }))
            {
                referenceSpaceChanged = false;
                read = "the library host ended";
                HasSession = false;
                return OpenXrSessionEnd.InstanceLost;
            }
            referenceSpaceChanged = changed;
            read = ShownText.Clean(text);
            return end;
        }

        public void DestroySession()
        {
            if (!_channel.Call(w => w.Int(OpenXrOps.DestroySession), ReadTail)) HasSession = false;
        }

        public void ReadHands(ref OpenXrHeadPose.Baseline left, ref OpenXrHeadPose.Baseline right,
                              out OpenXrHandState leftState, out OpenXrHandState rightState)
        {
            var l = left;
            var rt = right;
            OpenXrHandState ls = OpenXrHandState.Empty, rs = OpenXrHandState.Empty;
            if (!_channel.Call(w =>
                {
                    OpenXrHostService.WriteBaseline(w.Int(OpenXrOps.ReadHands), l);
                    OpenXrHostService.WriteBaseline(w, rt);
                }, r =>
                {
                    l = OpenXrHostService.ReadBaseline(r);
                    rt = OpenXrHostService.ReadBaseline(r);
                    ls = OpenXrHostService.ReadHand(r);
                    rs = OpenXrHostService.ReadHand(r);
                    ReadTail(r);
                }))
            {
                leftState = rightState = OpenXrHandState.Empty;
                return;
            }
            left = l;
            right = rt;
            leftState = ls;
            rightState = rs;
        }

        public bool TryLocateHead(out XrVector3f position, out XrQuaternionf orientation)
        {
            bool ok = false;
            XrVector3f p = default;
            XrQuaternionf q = default;
            if (!_channel.Call(w => w.Int(OpenXrOps.TryLocateHead), r =>
                {
                    ok = r.Bool();
                    float M() => r.Float(-(float)OpenXrOps.MaxMeters, (float)OpenXrOps.MaxMeters);
                    float Q() => r.Float(-OpenXrOps.MaxQuaternion, OpenXrOps.MaxQuaternion);
                    p = new XrVector3f { x = M(), y = M(), z = M() };
                    q = new XrQuaternionf { x = Q(), y = Q(), z = Q(), w = Q() };
                    ReadTail(r);
                }))
            {
                position = default;
                orientation = default;
                return false;
            }
            position = p;
            orientation = q;
            return ok;
        }

        public void Dispose()
        {
            _channel.Call(w => w.Int(OpenXrOps.Dispose), ReadTail);
            _channel.Dispose();
        }
    }
}
