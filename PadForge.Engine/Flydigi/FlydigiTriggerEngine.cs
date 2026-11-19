using System;

namespace PadForge.Engine.Flydigi
{
    /// <summary>
    /// Plays DualSense trigger effects on the two force triggers of an Apex
    /// 4 or Apex 5 (#586): it takes the block each trigger should show,
    /// the triggers' positions as they move and a clock, and hands the
    /// commands to send to a delegate.
    ///
    /// <para>A port of ApexSenseBridge's AdaptiveTriggerBridge.cpp. One
    /// block becomes one command through
    /// <see cref="FlydigiTriggerTranslator.Translate"/>. On an Apex 5 three
    /// kinds of block are then played from the PC, since the pad holds one
    /// effect per trigger: a bow swaps its draw for a break as the trigger
    /// passes the end zone, a zone curve swaps the effect as the trigger
    /// crosses a zone, and a Galloping or Machine block is stepped by
    /// <see cref="StepRhythm"/>. The Apex 4 keeps the static table, as it
    /// does there (apply, "identity &amp;&amp; !identity->isApex4()").</para>
    ///
    /// <para>A command equal to the last one sent to a trigger is not sent
    /// again. A block the table has no entry for leaves the trigger as it
    /// is. A game's block with type 0 does too: games interleave empty
    /// blocks with live effects, and releasing on each makes the trigger
    /// flutter (apply's comment on Call of Duty). PadForge's own Off is a
    /// release.</para>
    ///
    /// <para>Every method may be called from any thread.</para>
    /// </summary>
    public sealed class FlydigiTriggerEngine
    {
        /// <summary>The shortest time between two commands of one rhythm
        /// (kPeriodicMinimumSpacing).</summary>
        public const long RhythmMinimumSpacingMs = 10;

        private const int RhythmAmplitudeStep = 3;
        private const int RhythmPressOn = 8;
        private const int RhythmPressOff = 3;

        private readonly bool _emulate;
        private readonly Action<FlydigiTriggerCommand> _send;
        private readonly object _gate = new();
        private int _strengthPercent = 100;

        private sealed class Side
        {
            public readonly FlydigiTriggerSide Id;
            public FlydigiTriggerCommand? Last;
            public byte Position;

            public bool BowArmed;
            public readonly byte[] BowBlock = new byte[11];
            public FlydigiBow Bow;
            public bool Ruptured;

            public bool ZonesArmed;
            public readonly byte[] ZonesBlock = new byte[11];
            public FlydigiZones Zones;
            public FlydigiZoneTracker ZonesTracker;
            public FlydigiTriggerCommand ZonesSent;

            public bool RhythmArmed;
            public readonly byte[] RhythmBlock = new byte[11];
            public FlydigiRhythm Rhythm;
            public FlydigiZoneTracker RhythmTracker;
            public long RhythmEpochMs, RhythmLastSendMs;
            public FlydigiTriggerCommand RhythmSent;
            public bool Pressed;

            public Side(FlydigiTriggerSide id) => Id = id;

            public void Disarm()
            {
                BowArmed = false;
                ZonesArmed = false;
                RhythmArmed = false;
                Ruptured = false;
                Pressed = false;
            }
        }

        private readonly Side _left = new(FlydigiTriggerSide.Left);
        private readonly Side _right = new(FlydigiTriggerSide.Right);

        /// <param name="emulate">True for an Apex 5: bow, zone and rhythm
        /// effects are played from the PC.</param>
        /// <param name="send">Takes each command to send, in order. It
        /// must not block: the session queues it.</param>
        public FlydigiTriggerEngine(bool emulate, Action<FlydigiTriggerCommand> send)
        {
            _emulate = emulate;
            _send = send ?? throw new ArgumentNullException(nameof(send));
        }

        /// <summary>Scales every effect's force, 0 to 100.</summary>
        public int StrengthPercent
        {
            get { lock (_gate) return _strengthPercent; }
            set { lock (_gate) _strengthPercent = Math.Clamp(value, 0, 100); }
        }

        /// <summary>True while a rhythm is armed on either trigger, so
        /// <see cref="StepRhythm"/> has work. Read with no lock. The
        /// session's writer asks while it holds its queue, and every send
        /// here takes that queue while this class holds its gate
        /// (<see cref="SendLocked"/>), so a getter that took the gate and a
        /// send on another thread stopped each other for good. Both flags
        /// are written under the gate, and the session wakes its writer
        /// after a rhythm is armed, so the writer's next read sees
        /// it.</summary>
        public bool RhythmArmed
            => System.Threading.Volatile.Read(ref _left.RhythmArmed)
               || System.Threading.Volatile.Read(ref _right.RhythmArmed);

        private Side Of(FlydigiTriggerSide side) => side == FlydigiTriggerSide.Left ? _left : _right;

        /// <summary>Shows <paramref name="block"/>, an 11-byte DualSense
        /// trigger effect, on a trigger. <paramref name="ours"/> says the
        /// block is PadForge's own setting and no game's, so its type 0 is
        /// a release.</summary>
        public void Set(FlydigiTriggerSide side, ReadOnlySpan<byte> block, bool ours, long nowMs)
        {
            if (block.Length < 11) return;
            if (block[0] == 0)
            {
                if (ours) Clear(side);
                return;
            }
            if (!FlydigiTriggerTranslator.Translate(side, block, out var translated)) return;

            lock (_gate)
            {
                var s = Of(side);
                bool bow = false, zones = false, rhythm = false;
                FlydigiBow bowEffect = default;
                FlydigiZones zoneEffect = null;
                FlydigiRhythm rhythmEffect = default;
                if (_emulate)
                {
                    bow = FlydigiTriggerTranslator.DescribeBow(side, block, out bowEffect);
                    zones = FlydigiTriggerTranslator.DescribeZones(block, out zoneEffect);
                    rhythm = FlydigiTriggerTranslator.DescribeRhythm(block, out rhythmEffect);
                }

                if ((bow && s.BowArmed && block.Slice(0, 11).SequenceEqual(s.BowBlock))
                    || (zones && s.ZonesArmed && block.Slice(0, 11).SequenceEqual(s.ZonesBlock))
                    || (rhythm && s.RhythmArmed && block.Slice(0, 11).SequenceEqual(s.RhythmBlock)))
                    return;

                if (rhythm)
                {
                    var tracker = s.RhythmArmed ? s.RhythmTracker : new FlydigiZoneTracker(s.Position);
                    bool pressed = PressedWithHysteresis(s.RhythmArmed && s.Pressed, s.Position);
                    bool inRange = pressed && tracker.Zone >= rhythmEffect.FirstZone && tracker.Zone <= rhythmEffect.LastZone;
                    // The first command is rendered with the units of a
                    // trigger above the bottom, as the reference renders it.
                    translated = FlydigiTriggerTranslator.RenderRhythm(side, rhythmEffect, 0.0, inRange, 0);
                    block.Slice(0, 11).CopyTo(s.RhythmBlock);
                    s.Rhythm = rhythmEffect;
                    s.RhythmTracker = tracker;
                    s.RhythmEpochMs = nowMs;
                    s.RhythmLastSendMs = nowMs;
                    s.RhythmSent = translated;
                    s.Pressed = pressed;
                    s.RhythmArmed = true;
                }
                else s.RhythmArmed = false;

                if (bow)
                {
                    block.Slice(0, 11).CopyTo(s.BowBlock);
                    s.Bow = bowEffect;
                    s.Ruptured = false;
                    s.BowArmed = true;
                    translated = bowEffect.Draw;
                }
                else s.BowArmed = false;

                if (zones)
                {
                    var tracker = s.ZonesArmed ? s.ZonesTracker : new FlydigiZoneTracker(s.Position);
                    var zoneCommand = FlydigiTriggerTranslator.RenderZone(side, zoneEffect, tracker.Zone);
                    block.Slice(0, 11).CopyTo(s.ZonesBlock);
                    s.Zones = zoneEffect;
                    s.ZonesTracker = tracker;
                    s.ZonesSent = zoneCommand;
                    s.ZonesArmed = true;
                    translated = zoneCommand;
                }
                else s.ZonesArmed = false;

                SendLocked(s, FlydigiTriggerTranslator.ScaleStrength(translated, _strengthPercent));
            }
        }

        /// <summary>Releases a trigger.</summary>
        public void Clear(FlydigiTriggerSide side)
        {
            lock (_gate)
            {
                var s = Of(side);
                s.Disarm();
                SendLocked(s, FlydigiTriggerCommand.Off(side));
            }
        }

        /// <summary>The triggers' positions, 0 to 255. A bow or a zone
        /// curve armed on a trigger follows them
        /// (updateTriggerPositions).</summary>
        public void UpdatePositions(byte left, byte right)
        {
            lock (_gate)
            {
                Follow(_left, left);
                Follow(_right, right);
            }
        }

        private void Follow(Side s, byte position)
        {
            s.Position = position;
            FlydigiTriggerCommand? command = null;
            if (s.BowArmed)
            {
                if (!s.Ruptured && position >= s.Bow.RuptureAxis)
                {
                    command = s.Bow.Rupture;
                    s.Ruptured = true;
                }
                else if (s.Ruptured && position <= s.Bow.RearmAxis)
                {
                    command = s.Bow.Draw;
                    s.Ruptured = false;
                }
            }
            if (s.ZonesArmed)
            {
                int hysteresis = s.Zones.Frequency != 0
                    ? FlydigiTriggerTranslator.VibrationZoneHysteresis
                    : FlydigiTriggerTranslator.ZoneHysteresis;
                var rendered = FlydigiTriggerTranslator.RenderZone(s.Id, s.Zones, s.ZonesTracker.Update(position, hysteresis));
                if (rendered != s.ZonesSent)
                {
                    s.ZonesSent = rendered;
                    command = rendered;
                }
            }
            if (command.HasValue)
                SendLocked(s, FlydigiTriggerTranslator.ScaleStrength(command.Value, _strengthPercent));
        }

        /// <summary>Steps the armed rhythms. The session calls it every
        /// 2 ms while <see cref="RhythmArmed"/> (stepPeriodic).</summary>
        public void StepRhythm(long nowMs)
        {
            lock (_gate)
            {
                Step(_left, nowMs);
                Step(_right, nowMs);
            }
        }

        private void Step(Side s, long nowMs)
        {
            if (!s.RhythmArmed) return;
            int position = s.Position;
            int zone = s.RhythmTracker.Update((byte)position, FlydigiTriggerTranslator.VibrationZoneHysteresis);
            s.Pressed = PressedWithHysteresis(s.Pressed, position);
            bool inRange = s.Pressed && zone >= s.Rhythm.FirstZone && zone <= s.Rhythm.LastZone;
            double seconds = (nowMs - s.RhythmEpochMs) / 1000.0;
            var rendered = FlydigiTriggerTranslator.RenderRhythm(s.Id, s.Rhythm, seconds, inRange, position);
            if (!MeaningfullyDifferent(rendered, s.RhythmSent)) return;
            if (nowMs - s.RhythmLastSendMs < RhythmMinimumSpacingMs) return;
            s.RhythmSent = rendered;
            s.RhythmLastSendMs = nowMs;
            SendLocked(s, FlydigiTriggerTranslator.ScaleStrength(rendered, _strengthPercent), dedupe: false);
        }

        /// <summary>Forgets what the pad was sent, so the next block goes
        /// out whatever it is. For a pad that came back: it may or may not
        /// hold the effect it had (flydigi-apex4-linux triggers.py
        /// reopened).</summary>
        public void Forget()
        {
            lock (_gate)
            {
                _left.Last = null;
                _right.Last = null;
                _left.Disarm();
                _right.Disarm();
            }
        }

        private void SendLocked(Side s, FlydigiTriggerCommand command, bool dedupe = true)
        {
            if (dedupe && s.Last == command) return;
            s.Last = command;
            _send(command);
        }

        private static bool PressedWithHysteresis(bool wasPressed, int position)
            => wasPressed ? position > RhythmPressOff : position >= RhythmPressOn;

        /// <summary>Whether a rhythm's next command differs enough from the
        /// last to send: any change of mode or of a parameter, the strength
        /// by 3 or more (meaningfullyDifferent).</summary>
        internal static bool MeaningfullyDifferent(FlydigiTriggerCommand a, FlydigiTriggerCommand b)
        {
            if (a.Mode != b.Mode || a.Side != b.Side) return true;
            if (a.Mode != FlydigiTriggerMode.Rattle) return a != b;
            if (a.P0 != b.P0 || a.P1 != b.P1 || a.P3 != b.P3 || a.P4 != b.P4) return true;
            return Math.Abs(a.P2 - b.P2) >= RhythmAmplitudeStep;
        }
    }
}
