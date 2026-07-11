using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// Wire codec for the reverse output relay (issue #138). The machine running the
    /// game (consumer) computes the full, config-applied output for a shared device
    /// and ships a transport-agnostic SEMANTIC payload; the machine the device is
    /// plugged into (owner) re-encodes it for the real hardware and writes it. The
    /// owner synthesizes nothing — it is a pure transport transcoder.
    ///
    /// <para>Five kinds cover the effect-output channels:</para>
    /// <list type="bullet">
    /// <item>SonyEffect carries the transport-normalized DualSense effect report body
    ///   (47 B DS5 / 31 B DS4): rumble + adaptive triggers + lightbar + mic/player
    ///   LED + audio-control. Owner replays via SDL_SendGamepadEffect (re-frames
    ///   USB 0x02 / BT 0x31 + CRC).</item>
    /// <item>Vibration carries the full <see cref="Common.Vibration"/> (motors + impulse
    ///   triggers + directional + condition FFB) for every non-Sony, non-vendor-wheel
    ///   device. Owner replays via ForceFeedbackState.SetDeviceForces on the real
    ///   SDL handle (rumble / trigger rumble / DirectInput haptic, per its caps).</item>
    /// <item>Wheel carries semantic steering FFB (force/condition/periodic/range/autocenter/
    ///   RPM LEDs) for Logitech / Fanatec / Thrustmaster wheels. Owner re-encodes with
    ///   its own per-vendor raw-HID writer (vendor PID quantization + report sizing +
    ///   stateful upload/play caches stay on the machine that owns the wheel).</item>
    /// <item>HapticTone (#147) carries the consumer's HapticToneReducer output, one
    ///   (dominant frequency Hz, amplitude 0..1) pair per rumble tick, slot volume
    ///   already applied. Owner re-encodes with its own per-family writer (Joy-Con
    ///   HD Rumble / Steam 0x8f / Triton 0x83 / Deck), the Wheel division of labor.</item>
    /// <item>PlayerIndex (#191) carries the consumer's winning 1-based player number
    ///   (smallest displayed number across the slots the pad feeds) for NON-Sony
    ///   shared pads (Nintendo, BT DS3), whose player LED is otherwise
    ///   machine-local. DualSense/DS4 already carry the player LED inside the
    ///   SonyEffect body, so they never send this kind.</item>
    /// </list>
    /// Audio (the speaker sample stream) is carried out of band on its own
    /// <see cref="LinkMessageType.Audio"/> datagrams, not here.
    /// </summary>
    public static class OutputEffectCodec
    {
        public enum Kind : byte
        {
            SonyEffect = 1,
            Vibration = 2,
            Wheel = 3,
            // HD haptic tone (#147): the consumer's HapticToneReducer output,
            // one (dominant frequency Hz, amplitude 0..1) pair per rumble
            // tick, slot volume already applied. The owner re-encodes it with
            // its own per-family writer (Joy-Con HD Rumble / Steam 0x8f /
            // Triton 0x83 / Deck), exactly the Wheel division of labor.
            HapticTone = 4,
            // Player-index / player-LED number (#191) for non-Sony shared pads:
            // the consumer's winning 1-based global number (0 = unmapped; when
            // the pad feeds several consumer slots, the smallest displayed
            // number). The owner routes it to SetPlayerIndex (Nintendo) /
            // SetPlayerNumber (BT DS3).
            PlayerIndex = 5,
        }

        // ── Sony effect body ────────────────────────────────────────────────

        public static byte[] EncodeSonyEffect(ReadOnlySpan<byte> effectBody)
        {
            var b = new byte[1 + effectBody.Length];
            b[0] = (byte)Kind.SonyEffect;
            effectBody.CopyTo(b.AsSpan(1));
            return b;
        }

        // ── Full Vibration ──────────────────────────────────────────────────

        public static byte[] EncodeVibration(Vibration v)
        {
            var w = new Writer(64);
            w.U8((byte)Kind.Vibration);
            w.U16(v.LeftMotorSpeed);
            w.U16(v.RightMotorSpeed);
            w.U16(v.LeftTriggerMotorSpeed);
            w.U16(v.RightTriggerMotorSpeed);
            byte flags = (byte)((v.HasDirectionalData ? 1 : 0) | (v.HasConditionData ? 2 : 0));
            w.U8(flags);
            w.U32(v.EffectType);
            w.I16(v.SignedMagnitude);
            w.U16(v.Direction);
            w.U32(v.Period);
            w.U8(v.DeviceGain);
            int axes = v.HasConditionData && v.ConditionAxes != null
                ? Math.Min(v.ConditionAxisCount, v.ConditionAxes.Length) : 0;
            axes = Math.Min(axes, 2);
            w.U8((byte)axes);
            for (int i = 0; i < axes; i++)
            {
                var a = v.ConditionAxes[i];
                w.I16(a.PositiveCoefficient);
                w.I16(a.NegativeCoefficient);
                w.I16(a.Offset);
                w.U32(a.DeadBand);
                w.U32(a.PositiveSaturation);
                w.U32(a.NegativeSaturation);
            }
            return w.ToArray();
        }

        // ── Wheel FFB ───────────────────────────────────────────────────────

        /// <summary>The semantic steering-FFB frame (the consumer's WheelFfbSig plus
        /// rotation range and the resolved RPM-LED mask). Owner re-encodes per vendor.</summary>
        public static byte[] EncodeWheel(
            bool hasCond, bool dir, short force, short peak, int ac, uint effect, int period,
            short pc, short nc, short off, int db, int ps, int ns, int condGain,
            ushort rangeDeg, ushort ledMask, bool ledValid)
        {
            var w = new Writer(48);
            w.U8((byte)Kind.Wheel);
            w.U8((byte)((hasCond ? 1 : 0) | (dir ? 2 : 0) | (ledValid ? 4 : 0)));
            w.I16(force);
            w.I16(peak);
            w.I32(ac);
            w.U32(effect);
            w.I32(period);
            w.I16(pc);
            w.I16(nc);
            w.I16(off);
            w.I32(db);
            w.I32(ps);
            w.I32(ns);
            w.I32(condGain);
            w.U16(rangeDeg);
            w.U16(ledMask);
            return w.ToArray();
        }

        // ── HD haptic tone (#147) ───────────────────────────────────────────

        public static byte[] EncodeHapticTone(float toneHz, float amplitude)
        {
            var w = new Writer(9);
            w.U8((byte)Kind.HapticTone);
            w.U32(BitConverter.SingleToUInt32Bits(toneHz));
            w.U32(BitConverter.SingleToUInt32Bits(amplitude));
            return w.ToArray();
        }

        // ── Player index (#191) ─────────────────────────────────────────────

        /// <summary>The consumer slot's 1-based global player number (0 = unmapped).
        /// One byte suffices (16 VC slots max; the owner wraps %4 for the LED).</summary>
        public static byte[] EncodePlayerIndex(int oneBasedSlotNumber)
        {
            int n = oneBasedSlotNumber < 0 ? 0 : (oneBasedSlotNumber > 255 ? 255 : oneBasedSlotNumber);
            return new byte[] { (byte)Kind.PlayerIndex, (byte)n };
        }

        // ── Decode ──────────────────────────────────────────────────────────

        public readonly struct WheelFrame
        {
            public readonly bool HasCond, Dir, LedValid;
            public readonly short Force, Peak, Pc, Nc, Off;
            public readonly int Ac, Period, Db, Ps, Ns, CondGain;
            public readonly uint Effect;
            public readonly ushort RangeDeg, LedMask;
            public WheelFrame(bool hasCond, bool dir, bool ledValid, short force, short peak,
                short pc, short nc, short off, int ac, int period, int db, int ps, int ns,
                int condGain, uint effect, ushort rangeDeg, ushort ledMask)
            {
                HasCond = hasCond; Dir = dir; LedValid = ledValid; Force = force; Peak = peak;
                Pc = pc; Nc = nc; Off = off; Ac = ac; Period = period; Db = db; Ps = ps; Ns = ns;
                CondGain = condGain; Effect = effect; RangeDeg = rangeDeg; LedMask = ledMask;
            }
        }

        public readonly struct OutputEffect
        {
            public readonly Kind Kind;
            public readonly byte[] SonyBody;   // SonyEffect
            public readonly Vibration Vibration; // Vibration
            public readonly WheelFrame Wheel;  // Wheel
            public readonly float HapticToneHz;  // HapticTone
            public readonly float HapticToneAmp; // HapticTone
            public readonly int PlayerIndex;     // PlayerIndex (1-based, 0 = unmapped)
            public OutputEffect(Kind kind, byte[] sonyBody, Vibration vibration, WheelFrame wheel,
                float hapticToneHz = 0f, float hapticToneAmp = 0f, int playerIndex = 0)
            {
                Kind = kind; SonyBody = sonyBody; Vibration = vibration; Wheel = wheel;
                HapticToneHz = hapticToneHz; HapticToneAmp = hapticToneAmp; PlayerIndex = playerIndex;
            }
        }

        public static bool TryDecode(ReadOnlySpan<byte> payload, out OutputEffect effect)
        {
            effect = default;
            if (payload.Length < 1) return false;
            var kind = (Kind)payload[0];
            try
            {
                switch (kind)
                {
                    case Kind.SonyEffect:
                        if (payload.Length < 2 || payload.Length > 1 + 256) return false;
                        effect = new OutputEffect(kind, payload.Slice(1).ToArray(), null, default);
                        return true;

                    case Kind.Vibration:
                    {
                        var r = new Reader(payload, 1);
                        var v = new Vibration
                        {
                            LeftMotorSpeed = r.U16(),
                            RightMotorSpeed = r.U16(),
                            LeftTriggerMotorSpeed = r.U16(),
                            RightTriggerMotorSpeed = r.U16(),
                        };
                        byte flags = r.U8();
                        v.HasDirectionalData = (flags & 1) != 0;
                        v.HasConditionData = (flags & 2) != 0;
                        v.EffectType = r.U32();
                        v.SignedMagnitude = r.I16();
                        v.Direction = r.U16();
                        v.Period = r.U32();
                        v.DeviceGain = r.U8();
                        int axes = r.U8();
                        if (axes > 2) return false;
                        if (axes > 0)
                        {
                            var arr = new ConditionAxisData[axes];
                            for (int i = 0; i < axes; i++)
                                arr[i] = new ConditionAxisData
                                {
                                    PositiveCoefficient = r.I16(),
                                    NegativeCoefficient = r.I16(),
                                    Offset = r.I16(),
                                    DeadBand = r.U32(),
                                    PositiveSaturation = r.U32(),
                                    NegativeSaturation = r.U32(),
                                };
                            v.ConditionAxes = arr;
                            v.ConditionAxisCount = axes;
                        }
                        effect = new OutputEffect(kind, null, v, default);
                        return true;
                    }

                    case Kind.Wheel:
                    {
                        var r = new Reader(payload, 1);
                        byte flags = r.U8();
                        short force = r.I16();
                        short peak = r.I16();
                        int ac = r.I32();
                        uint eff = r.U32();
                        int period = r.I32();
                        short pc = r.I16();
                        short nc = r.I16();
                        short off = r.I16();
                        int db = r.I32();
                        int ps = r.I32();
                        int ns = r.I32();
                        int cg = r.I32();
                        ushort range = r.U16();
                        ushort led = r.U16();
                        var wf = new WheelFrame(
                            (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0,
                            force, peak, pc, nc, off, ac, period, db, ps, ns, cg, eff, range, led);
                        effect = new OutputEffect(kind, null, null, wf);
                        return true;
                    }

                    case Kind.HapticTone:
                    {
                        var r = new Reader(payload, 1);
                        float hz = BitConverter.UInt32BitsToSingle(r.U32());
                        float amp = BitConverter.UInt32BitsToSingle(r.U32());
                        if (!float.IsFinite(hz) || !float.IsFinite(amp)) return false;
                        effect = new OutputEffect(kind, null, null, default,
                            hapticToneHz: hz, hapticToneAmp: Math.Clamp(amp, 0f, 1f));
                        return true;
                    }

                    case Kind.PlayerIndex:
                    {
                        if (payload.Length < 2) return false;
                        effect = new OutputEffect(kind, null, null, default, playerIndex: payload[1]);
                        return true;
                    }

                    default:
                        return false;
                }
            }
            catch
            {
                effect = default;
                return false;
            }
        }

        // ── tiny LE serializers (bounds-checked decode) ─────────────────────

        private struct Writer
        {
            private readonly List<byte> _b;
            public Writer(int cap) { _b = new List<byte>(cap); }
            public void U8(byte v) => _b.Add(v);
            public void U16(ushort v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); }
            public void I16(short v) => U16(unchecked((ushort)v));
            public void U32(uint v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); _b.Add((byte)(v >> 16)); _b.Add((byte)(v >> 24)); }
            public void I32(int v) => U32(unchecked((uint)v));
            public byte[] ToArray() => _b.ToArray();
        }

        private ref struct Reader
        {
            private readonly ReadOnlySpan<byte> _s;
            private int _p;
            public Reader(ReadOnlySpan<byte> s, int start) { _s = s; _p = start; }
            public byte U8() { byte v = _s[_p]; _p += 1; return v; }
            public ushort U16() { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_s.Slice(_p, 2)); _p += 2; return v; }
            public short I16() => unchecked((short)U16());
            public uint U32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(_s.Slice(_p, 4)); _p += 4; return v; }
            public int I32() => unchecked((int)U32());
        }
    }
}
