using System;
using System.Collections.Concurrent;
using PadForge.Engine;
using PadForge.Engine.Steering;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Sends Vehicle mode's centering (#588) to a Logitech, a Fanatec or a
    /// Thrustmaster wheel through its own writer: the spring, the damper and
    /// the friction the wheel runs itself, and the centering force where the
    /// wheel has an effect to spare for it.
    ///
    /// <para>Where each goes, from the Linux drivers PadForge's writers follow:</para>
    /// <list type="bullet">
    ///   <item>Logitech: four force slots. The first holds the constant
    ///   force, and a spring, a damper or a friction force takes any free
    ///   one of the other three (new-lg4ff hid-lg4ff.c, lg4ff_init_slots
    ///   and the slot search in lg4ff_play_effect). PadForge gives the
    ///   spring the second, the damper the third and friction the fourth.
    ///   The game's force keeps the first slot, so the centering force is
    ///   added to it (<see cref="MergedForce"/>). A game's own spring,
    ///   damper or friction effect plays in the first slot too, and no
    ///   constant fits there with it, so while one plays the centering
    ///   force is a constant of its own in the fourth slot and friction,
    ///   whose slot that is, rests. Only the G27, G25, DFGT
    ///   and DFP have a friction force. The others take friction as a
    ///   damper with a full coefficient and a low limit, the cast
    ///   new-lg4ff makes for them.</item>
    ///   <item>Fanatec: five slots by kind, constant, spring, damper, inertia
    ///   and friction (hid-fanatecff hid-ftecff.c:1023-1036). The centering
    ///   force is added to the game's in the first. A game's own spring,
    ///   damper or friction takes that kind's slot while it plays, and
    ///   centering's comes back when it ends, or the slot is disabled where
    ///   centering has nothing for it. A game's inertia has a slot centering
    ///   never uses, disabled when the game's effect ends. Before this, a
    ///   game's condition effect stayed in its slot after the game stopped
    ///   it: the pass wrote the next constant force to the first slot and
    ///   nothing to the others. Spring mode's Auto Centering Strength is a
    ///   spring in the same slot as Vehicle mode's, since the base has no
    ///   centering of its own that PadForge can set. While it is on, the
    ///   slot is its own, and a write of it by the pass is told here
    ///   (<see cref="SpringSlotWritten"/>) so Vehicle mode's spring goes
    ///   back in when the slot is centering's again.</item>
    ///   <item>Thrustmaster: sixteen effects by id (hid-tmff2
    ///   hid-tmt300rs.c). The game's has the first, and centering has four
    ///   of its own, a constant force among them, so the wheel adds the
    ///   two forces itself.</item>
    /// </list>
    ///
    /// <para>Every effect is written only when it changes. The wheel holds
    /// what it was last sent, and each write is a blocking HID report. The
    /// first frame a Logitech or a Fanatec wheel gets writes every slot, a
    /// stop to each empty one, so nothing an earlier run left is in them.</para>
    /// </summary>
    internal static class WheelCenteringWriter
    {
        /// <summary>What a wheel was last sent of centering. -1 in a part
        /// stands for unknown: a game's effect had the slot, so the part is
        /// written again when the slot is free. GameInertia marks a Fanatec
        /// base whose inertia slot a game's effect holds. SlotForce is the
        /// centering force a Logitech wheel holds in its fourth slot while a
        /// game's effect has the first: 0 for none, the byte sent plus one,
        /// or -1 for unknown.</summary>
        private readonly record struct Applied(int SpringCoefficient, int SpringSaturation, int DamperCoefficient,
            int DamperSaturation, int FrictionSaturation, int Force, bool GameInertia, int SlotForce = 0);

        /// <summary>The Logitech slot friction has, which the centering
        /// force takes while a game's effect has the first.</summary>
        private const int FrictionSlot = 3;

        private static readonly ConcurrentDictionary<string, Applied> _applied = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The game condition effect a Fanatec base holds, as last
        /// written by <see cref="WriteFanatecGameForce"/>.</summary>
        private readonly record struct GameCondition(uint Effect, int PositiveCoefficient, int NegativeCoefficient,
            int Offset, int DeadBand, int PositiveSaturation, int NegativeSaturation, int Gain);

        private static readonly ConcurrentDictionary<string, GameCondition> _fanatecCondition = new(StringComparer.OrdinalIgnoreCase);

        private const int Unknown = -1;

        /// <summary>Unknown for the force, which runs below zero where the
        /// other parts never do.</summary>
        private const int UnknownForce = int.MinValue;
        private const int Logitech = 1, Fanatec = 2, Thrustmaster = 3;

        /// <summary>The steps a wheel's damper keeps its coefficient in:
        /// fifteen on a Logitech or a Fanatec wheel, whose firmware keeps it
        /// in four bits (new-lg4ff SCALE_COEFF(k, 4), hid-fanatecff the
        /// same), and hundredths on a Thrustmaster, which keeps sixteen
        /// bits.</summary>
        public static int DamperSteps(ushort vendorId, ushort productId)
            => LogitechRawHidWriter.IsLogitechWheel(vendorId, productId) || FanatecRawHidWriter.IsFanatecWheel(vendorId, productId)
                ? WheelCalibration.FourBitDamperSteps
                : WheelCalibration.FineDamperSteps;

        /// <summary>The effects centering has on a wheel while a game's own
        /// condition effect of this kind plays (FfbEffectTypes, or 0 for
        /// none), so the frame is composed for what the wheel will run. On a
        /// Fanatec base the game's effect has its kind's slot, and
        /// centering's effect of that kind rests. On a Logitech wheel the
        /// centering force has friction's slot (<see cref="Apply"/>), so
        /// friction rests. A Thrustmaster holds them all at once.</summary>
        public static CenteringEffects EffectsBeside(ushort vendorId, ushort productId, uint gameCondition)
        {
            var effects = CenteringEffects.All;
            if (gameCondition == 0) return effects;
            if (LogitechRawHidWriter.IsLogitechWheel(vendorId, productId)) return effects & ~CenteringEffects.Friction;
            if (!FanatecRawHidWriter.IsFanatecWheel(vendorId, productId)) return effects;
            return gameCondition switch
            {
                FfbEffectTypes.Spring => effects & ~CenteringEffects.Spring,
                FfbEffectTypes.Damper => effects & ~CenteringEffects.Damper,
                FfbEffectTypes.Friction => effects & ~CenteringEffects.Friction,
                _ => effects,
            };
        }

        /// <summary>Forgets what a wheel was sent, when it disconnects or
        /// its slot assignment ends, so the next frame is written whole.</summary>
        public static void ResetDevice(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return;
            _applied.TryRemove(devicePath, out _);
            _fanatecCondition.TryRemove(devicePath, out _);
        }

        /// <summary>The pass wrote a Fanatec base's spring slot itself, to
        /// set Spring mode's centering spring or to turn it off. What
        /// centering last put there is gone, so its spring is written
        /// again at the next frame in which the slot is centering's.</summary>
        public static void SpringSlotWritten(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return;
            if (_applied.TryGetValue(devicePath, out var prev) && prev.SpringCoefficient != Unknown)
                _applied[devicePath] = prev with { SpringCoefficient = Unknown };
            // A game's spring the slot held is gone with it, and is written again.
            if (_fanatecCondition.TryGetValue(devicePath, out var game) && game.Effect == FfbEffectTypes.Spring)
                _fanatecCondition.TryRemove(devicePath, out _);
        }

        /// <summary>The constant force a wheel is sent: the game's with the
        /// centering force added, on a wheel whose constant force has one
        /// slot. A Thrustmaster keeps the two apart, and a game's condition
        /// effect holds a Logitech's first slot, where no constant then
        /// fits: <see cref="Apply"/> gives the centering force the fourth
        /// slot while it does.</summary>
        public static short MergedForce(ushort vendorId, ushort productId, bool gameCondition, short gameForce, short centeringLevel)
        {
            if (centeringLevel == 0 || ThrustmasterRawHidWriter.IsThrustmasterWheel(vendorId, productId)) return gameForce;
            if (gameCondition && LogitechRawHidWriter.IsLogitechWheel(vendorId, productId)) return gameForce;
            return (short)Math.Clamp(gameForce + centeringLevel, -32767, 32767);
        }

        /// <summary>A Fanatec base's game effect and constant force, from this
        /// PC's pass or for a peer's wheel frame. The base plays every slot at
        /// once: hid-fanatecff sums each slot's effects on every tick,
        /// constants into the first and springs into the second
        /// (hid-ftecff.c ftecff_timer). So while a game's condition effect
        /// plays in its kind's slot, the first slot still takes the constant
        /// force, which carries Vehicle mode's centering force
        /// (<see cref="MergedForce"/>). The pass left the first slot at its
        /// last level then, and the centering force stood still. The game's
        /// condition is written when it is not the one the base holds, and
        /// the force when <paramref name="forceChanged"/> says so, so a
        /// centering force that moves does not send the game's effect
        /// again.</summary>
        public static void WriteFanatecGameForce(string devicePath, ushort productId, bool gameCondition, uint effect,
            int positiveCoefficient, int negativeCoefficient, int offset, int deadBand, int positiveSaturation,
            int negativeSaturation, int conditionGain, short force, int autoCenter, bool forceChanged)
        {
            if (string.IsNullOrEmpty(devicePath)) return;
            if (gameCondition)
            {
                var game = new GameCondition(effect, positiveCoefficient, negativeCoefficient, offset, deadBand,
                    positiveSaturation, negativeSaturation, conditionGain);
                if (!_fanatecCondition.TryGetValue(devicePath, out var held) || held != game)
                {
                    // A refused write leaves no record, so the next frame tries again.
                    if (FanatecRawHidWriter.WriteWheelCondition(devicePath, effect, positiveCoefficient, negativeCoefficient,
                            offset, deadBand, positiveSaturation, negativeSaturation, conditionGain))
                        _fanatecCondition[devicePath] = game;
                    else _fanatecCondition.TryRemove(devicePath, out _);
                }
                if (forceChanged) FanatecRawHidWriter.WriteWheelConstantForce(devicePath, force, productId);
                return;
            }
            // The game's condition ended. Its slot is centering's again (Apply).
            _fanatecCondition.TryRemove(devicePath, out _);
            FanatecRawHidWriter.WriteWheelConstantForce(devicePath, force, productId);
            // Re-assert the software centering spring each frame so it
            // survives a game-driven condition overwriting slot 1.
            if (autoCenter > 0)
            {
                FanatecRawHidWriter.WriteAutocenter(devicePath, autoCenter);
                SpringSlotWritten(devicePath);
            }
        }

        /// <summary>The constant force as it goes on the wire, so two levels
        /// that give the wheel the same bytes are one force: eight bits on a
        /// Logitech and on a Fanatec base without the high resolution quirk,
        /// sixteen on one with it, and half the level on a Thrustmaster. A
        /// level of 0 is its own value, since it stops the effect.</summary>
        public static int WireForce(ushort vendorId, ushort productId, short level)
        {
            if (level == 0) return int.MinValue;
            if (ThrustmasterRawHidWriter.IsThrustmasterWheel(vendorId, productId))
                return ThrustmasterRawHidWriter.ConstantLevel(level);
            if (FanatecRawHidWriter.IsFanatecWheel(vendorId, productId) && FanatecRawHidWriter.IsFanatecHighRes(productId))
                return level;
            return (level + 0x8000) >> 8;
        }

        /// <summary>Writes what changed of centering's effects.
        /// <paramref name="frame"/> is this poll's frame, empty in Spring
        /// mode, which lets go of whatever the wheel holds.
        /// <paramref name="gameCondition"/> is the kind of a game's own
        /// condition effect playing this poll (FfbEffectTypes), or 0.
        /// <paramref name="autoCenterSpring"/> says Spring mode's centering
        /// spring is on, which on a Fanatec base has the spring slot.</summary>
        public static void Apply(string devicePath, ushort vendorId, ushort productId, in CenteringFrame frame, uint gameCondition,
            bool autoCenterSpring = false)
        {
            if (string.IsNullOrEmpty(devicePath)) return;
            int vendor = LogitechRawHidWriter.IsLogitechWheel(vendorId, productId) ? Logitech
                : FanatecRawHidWriter.IsFanatecWheel(vendorId, productId) ? Fanatec
                : ThrustmasterRawHidWriter.IsThrustmasterWheel(vendorId, productId) ? Thrustmaster : 0;
            if (vendor == 0) return;

            bool had = _applied.TryGetValue(devicePath, out var prev);
            // A limit of 0 is the writers' word for no limit at all, so an effect with none is off, and an
            // effect that is off has no limit to compare.
            int springC = frame.SpringSaturation > 0 ? frame.SpringCoefficient : 0;
            int springS = springC > 0 ? frame.SpringSaturation : 0;
            int damperC = frame.DamperSaturation > 0 ? frame.DamperCoefficient : 0;
            int damperS = damperC > 0 ? frame.DamperSaturation : 0;
            int frictionS = frame.FrictionSaturation;
            // A game's condition effect on a Fanatec base holds a slot by its kind, and Spring mode's
            // centering spring holds the spring's.
            bool springHeld = vendor == Fanatec && (gameCondition == FfbEffectTypes.Spring || autoCenterSpring);
            bool gameHoldsSlot = vendor == Fanatec && gameCondition is FfbEffectTypes.Spring or FfbEffectTypes.Damper
                or FfbEffectTypes.Inertia or FfbEffectTypes.Friction;
            // A wheel holds what it was last sent until it is told otherwise, by a run of PadForge that was
            // killed among others. So the first frame writes every slot or id centering uses, the empty ones
            // with a stop, as the Linux drivers do when they take a wheel (new-lg4ff lg4ff_init_slots,
            // hid-fanatecff ftecff_init_slots). A Thrustmaster's effects play until they are stopped (a play
            // count of 0, hid-tmt300rs.c t300rs_play_effect), so its four ids are stopped too. An empty first
            // frame is no exception: a run killed while Vehicle mode or a Release Test held the slots left
            // them playing, and the next run may start in Spring mode, whose frames are empty.
            if (!had)
                prev = new Applied(Unknown, 0, Unknown, 0, Unknown, vendor == Thrustmaster ? UnknownForce : 0,
                    GameInertia: vendor == Fanatec);

            var next = prev;
            bool capFriction = vendor == Logitech && LogitechRawHidWriter.HasFrictionCap(productId);

            // The spring.
            if (springHeld) next = next with { SpringCoefficient = Unknown };
            else if (prev.SpringCoefficient != springC || prev.SpringSaturation != springS)
            {
                bool ok = WriteCondition(vendor, devicePath, FfbEffectTypes.Spring, springC, springS, capFriction);
                next = ok ? next with { SpringCoefficient = springC, SpringSaturation = springS }
                    : next with { SpringCoefficient = Unknown };
            }

            // The damper.
            if (vendor == Fanatec && gameCondition == FfbEffectTypes.Damper) next = next with { DamperCoefficient = Unknown };
            else if (prev.DamperCoefficient != damperC || prev.DamperSaturation != damperS)
            {
                bool ok = WriteCondition(vendor, devicePath, FfbEffectTypes.Damper, damperC, damperS, capFriction);
                next = ok ? next with { DamperCoefficient = damperC, DamperSaturation = damperS }
                    : next with { DamperCoefficient = Unknown };
            }

            // Friction, at a full coefficient under its limit. On a Logitech wheel a game's condition effect
            // has the first slot, where the centering force cannot join the game's (MergedForce), so the force
            // has friction's slot as a constant of its own while that effect plays. The slot is stopped
            // between the two, since each is downloaded afresh. A slot takes any kind of force: the game's
            // own condition effects play in the first, where new-lg4ff keeps the constant. No reference on
            // disk runs a constant in a slot other than the first, so its level goes at every slot's byte
            // (LogitechRawHidWriter.WriteConstantForce).
            if (vendor == Fanatec && gameCondition == FfbEffectTypes.Friction) next = next with { FrictionSaturation = Unknown };
            else if (vendor == Logitech && gameCondition != 0)
            {
                bool free = prev.FrictionSaturation == 0;
                if (!free)
                {
                    free = LogitechRawHidWriter.WriteStopEffect(devicePath, FrictionSlot);
                    next = next with { FrictionSaturation = free ? 0 : Unknown };
                }
                int wanted = frame.Level == 0 ? 0 : ((frame.Level + 0x8000) >> 8) + 1;
                if (free && prev.SlotForce != wanted)
                {
                    bool ok = wanted == 0
                        ? LogitechRawHidWriter.WriteStopEffect(devicePath, FrictionSlot)
                        : LogitechRawHidWriter.WriteConstantForce(devicePath, FrictionSlot, frame.Level, levelAtEverySlot: true);
                    next = next with { SlotForce = ok ? wanted : Unknown };
                }
            }
            else
            {
                bool free = prev.SlotForce == 0;
                if (!free)
                {
                    free = LogitechRawHidWriter.WriteStopEffect(devicePath, FrictionSlot);
                    next = next with { SlotForce = free ? 0 : Unknown };
                }
                if (free && prev.FrictionSaturation != frictionS)
                {
                    bool ok = WriteCondition(vendor, devicePath, FfbEffectTypes.Friction, frictionS > 0 ? 10000 : 0, frictionS, capFriction);
                    next = next with { FrictionSaturation = ok ? frictionS : Unknown };
                }
            }

            // A game's inertia on a Fanatec base, in the slot centering never uses.
            if (vendor == Fanatec)
            {
                if (gameCondition == FfbEffectTypes.Inertia) next = next with { GameInertia = true };
                else if (prev.GameInertia)
                {
                    bool ok = FanatecRawHidWriter.WriteWheelCondition(devicePath, FfbEffectTypes.Inertia, 0, 0, 0, 0, 0, 0, 100);
                    next = next with { GameInertia = !ok };
                }
            }

            // The centering force, where it has an effect of its own.
            if (vendor == Thrustmaster)
            {
                int wire = ThrustmasterRawHidWriter.ConstantLevel(frame.Level);
                if (prev.Force != wire)
                {
                    bool ok = ThrustmasterRawHidWriter.WriteConstantForce(devicePath, frame.Level, ThrustmasterRawHidWriter.CenteringForce);
                    next = next with { Force = ok ? wire : UnknownForce };
                }
            }

            if (!had || next != prev) _applied[devicePath] = next;
        }

        /// <summary>One condition effect in centering's own place on the
        /// wheel, symmetric about center. A coefficient of 0 stops it.</summary>
        private static bool WriteCondition(int vendor, string devicePath, uint kind, int coefficient, int saturation, bool capFriction)
        {
            switch (vendor)
            {
                case Logitech:
                {
                    int slot = kind == FfbEffectTypes.Spring ? 1 : kind == FfbEffectTypes.Damper ? 2 : 3;
                    return coefficient <= 0
                        ? LogitechRawHidWriter.WriteStopEffect(devicePath, slot)
                        : LogitechRawHidWriter.WriteCondition(devicePath, slot, kind, coefficient, coefficient, 0, 0,
                            saturation, saturation, 100, capFriction);
                }
                case Fanatec:
                    // The kind picks the slot, and a coefficient of 0 disables it (WriteWheelCondition).
                    return FanatecRawHidWriter.WriteWheelCondition(devicePath, kind, coefficient, coefficient, 0, 0,
                        saturation, saturation, 100);
                default:
                {
                    int id = kind == FfbEffectTypes.Spring ? ThrustmasterRawHidWriter.CenteringSpring
                        : kind == FfbEffectTypes.Damper ? ThrustmasterRawHidWriter.CenteringDamper
                        : ThrustmasterRawHidWriter.CenteringFriction;
                    // A coefficient of 0 stops the effect under the id (WriteCondition).
                    return ThrustmasterRawHidWriter.WriteCondition(devicePath, kind, coefficient, coefficient, 0, 0,
                        saturation, saturation, 100, id);
                }
            }
        }
    }
}
