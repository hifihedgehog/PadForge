using System.Diagnostics;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>
    /// When a frame that goes out on every pass goes out.
    ///
    /// <para>Most frames are held back while they say nothing new. Two
    /// kinds are sent on every pass. One carries a clock or a counter: it
    /// is never the same as the one before, and a reader can work from that
    /// clock, as SDL and DS4Windows time a DualShock 4's gyro by its frames'
    /// timestamps. The other is a VR state, whose driver PadForge has no
    /// idle contract with.</para>
    ///
    /// <para>Above <see cref="EveryPassUpToHz"/> passes a second, a frame a
    /// pass is more frames than any pad sends, and each is work for its
    /// reader: HIDMaestro's driver completes one HID read a frame (driver.c
    /// DeliverParked), and a DSU frame is a datagram to each client. So
    /// there a frame with nothing new waits until <see cref="Spacing"/>
    /// after the last one, which keeps a still pad near the 1000 frames a
    /// second it sends at 1000 passes, and a frame with something new goes
    /// out on the pass that made it.</para>
    /// </summary>
    internal sealed class EveryPassFrameGate
    {
        /// <summary>The rate up to which these frames go out on every pass,
        /// as they always have. The loop's schedule is absolute, so at this
        /// rate a pass after a late wake comes sooner than
        /// <see cref="Spacing"/> after the one before, and a gate that held
        /// its frame back would thin the frames of the default rate.</summary>
        internal const int EveryPassUpToHz = 1000;

        /// <summary>Whether the poll loop runs above
        /// <see cref="EveryPassUpToHz"/>. The loop sets it as it reads its
        /// rate, and each sender asks it before it compares anything, so a
        /// rate at or under the mark pays nothing for the gate.</summary>
        internal static volatile bool Engaged;

        /// <summary>The least time between two frames that say the same,
        /// in <see cref="Stopwatch"/> ticks: three quarters of a
        /// millisecond. Windows wakes the loop on a half millisecond grid,
        /// so the frames of a still pad land a millisecond apart, and a
        /// wake a little early still counts.</summary>
        internal static readonly long Spacing = Stopwatch.Frequency * 3 / 4000;

        private long _sentAt;
        private bool _sent;

        /// <summary>Whether a frame goes out at <paramref name="now"/>:
        /// always when it says something the last one did not, and
        /// otherwise once <see cref="Spacing"/> has passed since that
        /// one.</summary>
        internal bool Due(bool changed, long now)
        {
            if (_sent && !changed && now - _sentAt < Spacing) return false;
            _sentAt = now;
            _sent = true;
            return true;
        }
    }

    /// <summary>
    /// Whether two states say the same thing to a reader: every field but a
    /// clock. Field by field, since a struct's bytes hold padding nobody
    /// set. FrameContentTests changes each field in turn, so a field added
    /// to one of these states and left out here fails by name.
    /// </summary>
    internal static class FrameContent
    {
        internal static bool Same(in Gamepad a, in Gamepad b)
            => a.Buttons == b.Buttons
            && a.LeftTrigger == b.LeftTrigger
            && a.RightTrigger == b.RightTrigger
            && a.ThumbLX == b.ThumbLX
            && a.ThumbLY == b.ThumbLY
            && a.ThumbRX == b.ThumbRX
            && a.ThumbRY == b.ThumbRY
            && a.Share == b.Share
            && a.MicMute == b.MicMute
            && a.LeftPaddle == b.LeftPaddle
            && a.RightPaddle == b.RightPaddle
            && a.LeftFunction == b.LeftFunction
            && a.RightFunction == b.RightFunction;

        internal static bool Same(in TouchpadState a, in TouchpadState b)
            => a.X0 == b.X0 && a.Y0 == b.Y0
            && a.X1 == b.X1 && a.Y1 == b.Y1
            && a.Down0 == b.Down0 && a.Down1 == b.Down1
            && a.Click == b.Click
            && a.PacketCounter == b.PacketCounter;

        /// <summary>The sensor's reading. Its timestamp is the clock, and
        /// moves on every pass.</summary>
        internal static bool Same(in MotionSnapshot a, in MotionSnapshot b)
            => a.HasMotion == b.HasMotion
            && a.GyroPitch == b.GyroPitch && a.GyroYaw == b.GyroYaw && a.GyroRoll == b.GyroRoll
            && a.AccelX == b.AccelX && a.AccelY == b.AccelY && a.AccelZ == b.AccelZ;

        internal static bool Same(in ButtonPressureState a, in ButtonPressureState b)
            => a.ButtonA == b.ButtonA && a.ButtonB == b.ButtonB
            && a.ButtonX == b.ButtonX && a.ButtonY == b.ButtonY
            && a.LeftShoulder == b.LeftShoulder && a.RightShoulder == b.RightShoulder
            && a.DPadUp == b.DPadUp && a.DPadDown == b.DPadDown
            && a.DPadLeft == b.DPadLeft && a.DPadRight == b.DPadRight;

        internal static bool Same(in VrRawState a, in VrRawState b)
            => Same(in a.Left, in b.Left) && Same(in a.Right, in b.Right);

        internal static bool Same(in VrHandRaw a, in VrHandRaw b)
            => a.Buttons == b.Buttons
            && a.Trigger == b.Trigger && a.Grip == b.Grip
            && a.StickX == b.StickX && a.StickY == b.StickY;
    }
}
