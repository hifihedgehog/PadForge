using System;
using System.Buffers.Binary;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// Compact wire codec for <see cref="CustomInputState"/> — the payload the
    /// Remote Link transport seals into each datagram (issue #138).
    ///
    /// Every frame is absolute (full state, not a delta): the decoder resets to a
    /// neutral baseline and applies only the blocks the frame carries, so any
    /// single frame fully reconstructs the state with no dependence on the
    /// previous one. That is what makes newest-wins datagram loss self-healing —
    /// a dropped frame is simply superseded.
    ///
    /// Compactness without giving up absoluteness: a present-block bitmask omits
    /// whole blocks, and within the analog blocks each element is sent only when
    /// it differs from its neutral value (stick center, trigger zero, POV
    /// centered). A centered gamepad with nothing pressed encodes to 3 bytes.
    ///
    /// Encoder and decoder share <see cref="AxisNeutral"/>; that definition only
    /// affects how compact an idle device is, never correctness — an omitted
    /// element always decodes back to exactly the neutral the encoder skipped.
    /// </summary>
    public static class CustomInputStateCodec
    {
        /// <summary>Codec format version. Bump only on an incompatible layout change.</summary>
        public const byte Version = 1;

        [Flags]
        private enum Block : ushort
        {
            None = 0,
            Axis = 1 << 0,
            Sliders = 1 << 1,
            Povs = 1 << 2,
            Buttons = 1 << 3,
            Gyro = 1 << 4,
            Accel = 1 << 5,
            Battery = 1 << 6,
            Touchpad = 1 << 7,
            Midi = 1 << 8,
            // Post-3.5.0 state families (issues #146/#151/#154). Appended
            // AFTER every older block in the frame, which keeps mixed
            // versions compatible without a Version bump: an old decoder
            // reads its known blocks in order, never reaches these, and
            // its final `o <= payload.Length` check tolerates the tail.
            Ir = 1 << 9,
            JoyConIr = 1 << 10,
            JoyCon2Mouse = 1 << 11,
            // Aux (left-side) accelerometer, issue #199: Nunchuk / left Joy-Con.
            AccelAux = 1 << 12,
        }

        /// <summary>
        /// Which optional sensor blocks the device exposes. Gyro and accel arrays
        /// are always allocated on <see cref="CustomInputState"/> and zeroed when
        /// absent, so their presence cannot be inferred from the state — the
        /// negotiated capability says whether to send them. Touchpad, MIDI, and
        /// battery presence are inferred from the state directly.
        /// </summary>
        public readonly struct Caps
        {
            public Caps(bool gyro, bool accel, bool accelAux = false)
            { Gyro = gyro; Accel = accel; AccelAux = accelAux; }
            public bool Gyro { get; }
            public bool Accel { get; }
            /// <summary>Aux (left-side) accelerometer: Nunchuk / left Joy-Con (#199).</summary>
            public bool AccelAux { get; }
        }

        /// <summary>Neutral (idle) value for axis index <paramref name="i"/>:
        /// 32768 for the four stick axes (LX/LY/RX/RY), 0 for triggers and extras.</summary>
        public static int AxisNeutral(int i) => (i == 0 || i == 1 || i == 3 || i == 4) ? 32768 : 0;

        // ── Encode ──────────────────────────────────────────────────────────

        /// <summary>Encode into <paramref name="destination"/>, returning the byte count.
        /// Throws if the buffer is smaller than <see cref="MaxEncodedSize"/> for the state.</summary>
        public static int Encode(CustomInputState state, Caps caps, Span<byte> destination)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            int o = 0;
            destination[o++] = Version;
            int presenceAt = o;
            o += 2; // presence u16 backfilled at the end

            Block present = Block.None;

            // Axis: 24-bit sub-mask + u16 per non-neutral axis.
            {
                Span<byte> mask = stackalloc byte[3];
                int count = 0;
                for (int i = 0; i < CustomInputState.MaxAxis; i++)
                    if (state.Axis[i] != AxisNeutral(i)) { mask[i >> 3] |= (byte)(1 << (i & 7)); count++; }
                if (count > 0)
                {
                    present |= Block.Axis;
                    mask.CopyTo(destination.Slice(o, 3)); o += 3;
                    for (int i = 0; i < CustomInputState.MaxAxis; i++)
                        if (state.Axis[i] != AxisNeutral(i)) o += WriteU16(destination, o, state.Axis[i]);
                }
            }

            // Sliders: 8-bit sub-mask + u16 per non-zero slider.
            {
                byte mask = 0; int count = 0;
                for (int i = 0; i < CustomInputState.MaxSliders; i++)
                    if (state.Sliders[i] != 0) { mask |= (byte)(1 << i); count++; }
                if (count > 0)
                {
                    present |= Block.Sliders;
                    destination[o++] = mask;
                    for (int i = 0; i < CustomInputState.MaxSliders; i++)
                        if (state.Sliders[i] != 0) o += WriteU16(destination, o, state.Sliders[i]);
                }
            }

            // POVs: 4-bit sub-mask + u16 per non-centered hat (centered = -1 is the neutral).
            {
                byte mask = 0; int count = 0;
                for (int i = 0; i < CustomInputState.MaxPovs; i++)
                    if (state.Povs[i] != -1) { mask |= (byte)(1 << i); count++; }
                if (count > 0)
                {
                    present |= Block.Povs;
                    destination[o++] = mask;
                    for (int i = 0; i < CustomInputState.MaxPovs; i++)
                        if (state.Povs[i] != -1) o += WriteU16(destination, o, state.Povs[i]);
                }
            }

            // Buttons: length-prefixed LSB-first bitmask, trimmed to the highest set bit.
            {
                int highest = -1;
                for (int i = 0; i < CustomInputState.MaxButtons; i++) if (state.Buttons[i]) highest = i;
                if (highest >= 0)
                {
                    present |= Block.Buttons;
                    int maskLen = (highest >> 3) + 1;
                    destination[o++] = (byte)maskLen;
                    var bytes = destination.Slice(o, maskLen);
                    bytes.Clear();
                    for (int i = 0; i <= highest; i++)
                        if (state.Buttons[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
                    o += maskLen;
                }
            }

            if (caps.Gyro)
            {
                present |= Block.Gyro;
                for (int i = 0; i < 3; i++) { BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(o, 4), state.Gyro[i]); o += 4; }
            }

            if (caps.Accel)
            {
                present |= Block.Accel;
                for (int i = 0; i < 3; i++) { BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(o, 4), state.Accel[i]); o += 4; }
            }

            // Charging alone is enough to send the block: a pad that reports
            // "charging, percent unknown" must not lose the flag on the wire.
            if (state.BatteryPercent >= 0 || state.BatteryCharging)
            {
                present |= Block.Battery;
                destination[o++] = (byte)(sbyte)Math.Clamp(state.BatteryPercent, -1, 100);
                destination[o++] = (byte)(state.BatteryCharging ? 1 : 0);
            }

            if (state.Touchpads != null && state.Touchpads.Length > 0)
            {
                present |= Block.Touchpad;
                destination[o++] = (byte)Math.Min(state.Touchpads.Length, 255);
                int padCount = Math.Min(state.Touchpads.Length, 255);
                for (int p = 0; p < padCount; p++)
                {
                    var pad = state.Touchpads[p];
                    // Clamp once and reuse for both the header byte and the loop
                    // bound, mirroring the pad-count path above, so decode (which
                    // reads the header byte) consumes exactly what encode wrote.
                    int fingers = Math.Min(pad?.MaxFingers ?? 0, 255);
                    destination[o++] = (byte)fingers;
                    destination[o++] = (byte)((pad != null && pad.Clicked) ? 1 : 0);
                    for (int f = 0; f < fingers; f++)
                    {
                        destination[o++] = (byte)(pad.FingerDown[f] ? 1 : 0);
                        BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(o, 2), (short)Math.Clamp(pad.FingerContactId[f], short.MinValue, short.MaxValue)); o += 2;
                        o += WriteU16(destination, o, NormToU16(pad.FingerX[f]));
                        o += WriteU16(destination, o, NormToU16(pad.FingerY[f]));
                        o += WriteU16(destination, o, NormToU16(pad.FingerPressure[f]));
                    }
                }
            }

            if (state.Midi != null)
            {
                present |= Block.Midi;
                var midi = state.Midi;
                o += WriteBitmask128(destination, o, midi.Notes);
                o += WriteBitmask128(destination, o, midi.CcUp);
                o += WriteBitmask128(destination, o, midi.CcDown);
                int ccAt = o++; int ccCount = 0;
                for (int i = 0; i < MidiInputState.CcCount; i++)
                    if (midi.Cc[i] != 0) { destination[o++] = (byte)i; destination[o++] = midi.Cc[i]; ccCount++; }
                destination[ccAt] = (byte)ccCount;
                o += WriteU16(destination, o, midi.PitchBend);
            }

            // New blocks stay strictly after every pre-#146 block (see the
            // Block enum note on mixed-version compatibility).

            // Wii IR pointer (#146): sent only while a dot is detected; an
            // omitted block decodes to the neutral "no IR this frame".
            if (state.Ir.Detected)
            {
                present |= Block.Ir;
                BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(o, 4), state.Ir.X); o += 4;
                BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(o, 4), state.Ir.Y); o += 4;
            }

            // Joy-Con NIR intensity (#151): 0 means camera off / absent.
            if (state.JoyConIrIntensity != 0f)
            {
                present |= Block.JoyConIr;
                BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(o, 4), state.JoyConIrIntensity); o += 4;
            }

            // Joy-Con 2 mouse deltas (#154): per-poll counts, 0 when idle.
            if (state.JoyCon2MouseDX != 0f || state.JoyCon2MouseDY != 0f)
            {
                present |= Block.JoyCon2Mouse;
                BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(o, 4), state.JoyCon2MouseDX); o += 4;
                BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(o, 4), state.JoyCon2MouseDY); o += 4;
            }

            // Aux (left-side) accelerometer (#199): Nunchuk / left Joy-Con.
            // Capability-gated like Gyro/Accel (a zeroed array is not "absent").
            if (caps.AccelAux)
            {
                present |= Block.AccelAux;
                for (int i = 0; i < 3; i++) { BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(o, 4), state.AccelAux[i]); o += 4; }
            }

            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(presenceAt, 2), (ushort)present);
            return o;
        }

        /// <summary>Encode to a right-sized array (convenience; allocates).</summary>
        public static byte[] Encode(CustomInputState state, Caps caps)
        {
            var buffer = new byte[MaxEncodedSize(state, caps)];
            int n = Encode(state, caps, buffer);
            return buffer[..n];
        }

        /// <summary>Conservative upper bound on the encoded size for this state — exact-allocation safe.</summary>
        public static int MaxEncodedSize(CustomInputState state, Caps caps)
        {
            int size = 3; // version + presence
            size += 3 + CustomInputState.MaxAxis * 2;
            size += 1 + CustomInputState.MaxSliders * 2;
            size += 1 + CustomInputState.MaxPovs * 2;
            size += 1 + (CustomInputState.MaxButtons / 8);
            if (caps.Gyro) size += 12;
            if (caps.Accel) size += 12;
            if (caps.AccelAux) size += 12;
            size += 2; // battery
            if (state?.Touchpads != null)
            {
                size += 1;
                foreach (var pad in state.Touchpads) size += 2 + (pad?.MaxFingers ?? 0) * 9;
            }
            if (state?.Midi != null) size += 48 + 1 + MidiInputState.CcCount * 2 + 2;
            size += 8 + 4 + 8; // Ir (2 floats) + JoyConIr (1 float) + JoyCon2Mouse (2 floats)
            return size;
        }

        // ── Decode ──────────────────────────────────────────────────────────

        /// <summary>Decode into a fresh state (convenience; allocates).</summary>
        public static CustomInputState Decode(ReadOnlySpan<byte> payload)
        {
            var state = new CustomInputState();
            DecodeInto(payload, state);
            return state;
        }

        /// <summary>A fresh state at the wire neutral (sticks centered at 32768,
        /// triggers/sliders 0, POVs centered, nothing pressed). This is what an
        /// empty frame decodes to, and the right starting point for a receive-side
        /// buffer — a default-constructed CustomInputState leaves sticks at 0.</summary>
        public static CustomInputState CreateNeutral()
        {
            var state = new CustomInputState();
            ResetToNeutral(state);
            return state;
        }

        /// <summary>
        /// Decode into a reusable target (pooling-friendly: no allocation when the
        /// target's MIDI / touchpad shape already matches). Resets the target to
        /// neutral first, so the result depends only on this frame. Returns false
        /// on a malformed, truncated, or unknown-version payload, leaving the
        /// target reset-to-neutral rather than half-applied.
        /// </summary>
        public static bool DecodeInto(ReadOnlySpan<byte> payload, CustomInputState target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            ResetToNeutral(target);

            try
            {
                int o = 0;
                if (payload.Length < 3) return false;
                if (payload[o++] != Version) return false;
                var present = (Block)BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(o, 2)); o += 2;

                if ((present & Block.Axis) != 0)
                {
                    var mask = payload.Slice(o, 3); o += 3;
                    for (int i = 0; i < CustomInputState.MaxAxis; i++)
                        if ((mask[i >> 3] & (1 << (i & 7))) != 0) { target.Axis[i] = ReadU16(payload, ref o); }
                }

                if ((present & Block.Sliders) != 0)
                {
                    byte mask = payload[o++];
                    for (int i = 0; i < CustomInputState.MaxSliders; i++)
                        if ((mask & (1 << i)) != 0) target.Sliders[i] = ReadU16(payload, ref o);
                }

                if ((present & Block.Povs) != 0)
                {
                    byte mask = payload[o++];
                    for (int i = 0; i < CustomInputState.MaxPovs; i++)
                        if ((mask & (1 << i)) != 0) target.Povs[i] = ReadU16(payload, ref o);
                }

                if ((present & Block.Buttons) != 0)
                {
                    int maskLen = payload[o++];
                    var bytes = payload.Slice(o, maskLen); o += maskLen;
                    int bits = Math.Min(maskLen * 8, CustomInputState.MaxButtons);
                    for (int i = 0; i < bits; i++)
                        target.Buttons[i] = (bytes[i >> 3] & (1 << (i & 7))) != 0;
                }

                if ((present & Block.Gyro) != 0)
                    for (int i = 0; i < 3; i++) { target.Gyro[i] = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(o, 4)); o += 4; }

                if ((present & Block.Accel) != 0)
                    for (int i = 0; i < 3; i++) { target.Accel[i] = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(o, 4)); o += 4; }

                if ((present & Block.Battery) != 0)
                {
                    target.BatteryPercent = (sbyte)payload[o++];
                    target.BatteryCharging = payload[o++] != 0;
                }

                if ((present & Block.Touchpad) != 0)
                {
                    int padCount = payload[o++];
                    EnsureTouchpadShape(target, padCount, payload, o);
                    for (int p = 0; p < padCount; p++)
                    {
                        int fingers = payload[o++];
                        bool clicked = payload[o++] != 0;
                        var pad = target.Touchpads[p];
                        if (pad != null) pad.Clicked = clicked;
                        for (int f = 0; f < fingers; f++)
                        {
                            bool down = payload[o++] != 0;
                            short cid = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(o, 2)); o += 2;
                            int x = ReadU16(payload, ref o);
                            int y = ReadU16(payload, ref o);
                            int pr = ReadU16(payload, ref o);
                            if (pad != null && f < pad.MaxFingers)
                            {
                                pad.FingerDown[f] = down;
                                pad.FingerContactId[f] = cid;
                                pad.FingerX[f] = U16ToNorm(x);
                                pad.FingerY[f] = U16ToNorm(y);
                                pad.FingerPressure[f] = U16ToNorm(pr);
                            }
                        }
                    }
                }

                if ((present & Block.Midi) != 0)
                {
                    target.Midi ??= new MidiInputState();
                    var midi = target.Midi;
                    Array.Clear(midi.Notes); Array.Clear(midi.Cc); Array.Clear(midi.CcUp); Array.Clear(midi.CcDown);
                    o += ReadBitmask128(payload, o, midi.Notes);
                    o += ReadBitmask128(payload, o, midi.CcUp);
                    o += ReadBitmask128(payload, o, midi.CcDown);
                    int ccCount = payload[o++];
                    for (int i = 0; i < ccCount; i++) { int idx = payload[o++]; byte val = payload[o++]; if (idx < MidiInputState.CcCount) midi.Cc[idx] = val; }
                    midi.PitchBend = ReadU16(payload, ref o);
                }

                if ((present & Block.Ir) != 0)
                {
                    target.Ir = new WiiIrState
                    {
                        X = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(o, 4)),
                        Y = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(o + 4, 4)),
                        Detected = true,
                    };
                    o += 8;
                }

                if ((present & Block.JoyConIr) != 0)
                {
                    target.JoyConIrIntensity = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(o, 4));
                    o += 4;
                }

                if ((present & Block.JoyCon2Mouse) != 0)
                {
                    target.JoyCon2MouseDX = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(o, 4));
                    target.JoyCon2MouseDY = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(o + 4, 4));
                    o += 8;
                }

                if ((present & Block.AccelAux) != 0)
                    for (int i = 0; i < 3; i++) { target.AccelAux[i] = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(o, 4)); o += 4; }

                return o <= payload.Length;
            }
            catch
            {
                // Any malformed, truncated, or oversized field fails closed:
                // span indexing throws IndexOutOfRangeException and slicing throws
                // ArgumentOutOfRangeException, so a network parser facing hostile
                // input catches both (and anything else) and leaves the target
                // reset-to-neutral rather than crashing or half-applied.
                ResetToNeutral(target);
                return false;
            }
        }

        private static void ResetToNeutral(CustomInputState s)
        {
            for (int i = 0; i < CustomInputState.MaxAxis; i++) s.Axis[i] = AxisNeutral(i);
            Array.Clear(s.Sliders);
            for (int i = 0; i < CustomInputState.MaxPovs; i++) s.Povs[i] = -1;
            Array.Clear(s.Buttons);
            Array.Clear(s.Gyro);
            Array.Clear(s.Accel);
            Array.Clear(s.AccelAux);
            s.BatteryPercent = -1;
            s.BatteryCharging = false;
            s.Ir = default;
            s.JoyConIrIntensity = 0f;
            s.JoyCon2MouseDX = 0f;
            s.JoyCon2MouseDY = 0f;
            if (s.Midi != null)
            {
                // The decode contract promises "reset-to-neutral rather than
                // half-applied"; without this the previous frame's MIDI state
                // survived a malformed frame or a link drop.
                Array.Clear(s.Midi.Notes); Array.Clear(s.Midi.Cc);
                Array.Clear(s.Midi.CcUp); Array.Clear(s.Midi.CcDown);
                s.Midi.PitchBend = MidiInputState.PitchBendCenter;
            }
            if (s.Touchpads != null)
                foreach (var pad in s.Touchpads)
                    if (pad != null)
                    {
                        pad.Clicked = false;
                        for (int f = 0; f < pad.MaxFingers; f++)
                        {
                            pad.FingerDown[f] = false; pad.FingerContactId[f] = -1;
                            pad.FingerX[f] = 0; pad.FingerY[f] = 0; pad.FingerPressure[f] = 0;
                        }
                    }
        }

        private static void EnsureTouchpadShape(CustomInputState target, int padCount, ReadOnlySpan<byte> payload, int afterPadCountOffset)
        {
            // Peek each pad's maxFingers to allocate a matching shape only when it
            // differs, so the steady state reuses the existing arrays.
            bool matches = target.Touchpads != null && target.Touchpads.Length == padCount;
            int peek = afterPadCountOffset;
            var fingerCounts = padCount > 0 ? new int[padCount] : Array.Empty<int>();
            for (int p = 0; p < padCount; p++)
            {
                int fingers = payload[peek];
                fingerCounts[p] = fingers;
                if (matches && (target.Touchpads[p]?.MaxFingers ?? -1) != fingers) matches = false;
                peek += 2 + fingers * 9; // maxFingers + clicked + fingers*(down+cid2+x2+y2+pr2)
            }
            if (matches) return;
            var pads = new TouchpadInputState[padCount];
            for (int p = 0; p < padCount; p++) pads[p] = new TouchpadInputState(fingerCounts[p]);
            target.Touchpads = pads;
        }

        // ── Field helpers ───────────────────────────────────────────────────

        private static int WriteU16(Span<byte> dst, int offset, int value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(offset, 2), (ushort)Math.Clamp(value, 0, 65535));
            return 2;
        }

        private static int ReadU16(ReadOnlySpan<byte> src, ref int offset)
        {
            int v = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(offset, 2));
            offset += 2;
            return v;
        }

        private static int WriteBitmask128(Span<byte> dst, int offset, bool[] bits)
        {
            var span = dst.Slice(offset, 16);
            span.Clear();
            int n = Math.Min(bits.Length, 128);
            for (int i = 0; i < n; i++) if (bits[i]) span[i >> 3] |= (byte)(1 << (i & 7));
            return 16;
        }

        private static int ReadBitmask128(ReadOnlySpan<byte> src, int offset, bool[] bits)
        {
            var span = src.Slice(offset, 16);
            int n = Math.Min(bits.Length, 128);
            for (int i = 0; i < n; i++) bits[i] = (span[i >> 3] & (1 << (i & 7))) != 0;
            return 16;
        }

        private static int NormToU16(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 65535f);
        private static float U16ToNorm(int v) => v / 65535f;
    }
}
