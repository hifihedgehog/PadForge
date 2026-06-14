using System;
using System.Buffers.Binary;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// Wire codec for the reverse feedback channel (issue #138 M2). A consumer's
    /// game produces output (rumble, adaptive triggers, lightbar, mic/player LED)
    /// for a shared controller; the consumer encodes it with this codec and sends
    /// a <see cref="LinkMessageType.Output"/> datagram to the device's owner, who
    /// decodes it and drives the physical hardware.
    ///
    /// <para>Two payload kinds. Rumble carries the four XInput motor magnitudes for
    /// non-Sony pads. SonyEffect carries the raw, transport-normalized DualSense
    /// effect report body (47 bytes DS5, 31 bytes DS4) verbatim — the same bytes
    /// HIDMaestro hands PadForge at the virtual-controller output, which the owner
    /// replays through SDL_SendGamepadEffect. The owner re-frames per its own
    /// device's transport, so the wire form stays device-agnostic.</para>
    ///
    /// Layout (after the LinkSession AEAD has been opened):
    ///   [0]      kind (1 = Rumble, 2 = SonyEffect)
    ///   Rumble:     [1..2] left u16 LE, [3..4] right u16 LE,
    ///               [5..6] leftTrigger u16 LE, [7..8] rightTrigger u16 LE
    ///   SonyEffect: [1..]  the raw effect report body
    /// </summary>
    public static class OutputEffectCodec
    {
        public enum Kind : byte
        {
            Rumble = 1,
            SonyEffect = 2,
        }

        /// <summary>Encode the four XInput motor magnitudes (0..65535) for a non-Sony pad.</summary>
        public static byte[] EncodeRumble(ushort left, ushort right, ushort leftTrigger, ushort rightTrigger)
        {
            var b = new byte[9];
            b[0] = (byte)Kind.Rumble;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1, 2), left);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3, 2), right);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5, 2), leftTrigger);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(7, 2), rightTrigger);
            return b;
        }

        /// <summary>Encode a raw DualSense effect report body (the BT-&gt;USB normalized
        /// bytes captured at the virtual controller's output).</summary>
        public static byte[] EncodeSonyEffect(ReadOnlySpan<byte> effectPayload)
        {
            var b = new byte[1 + effectPayload.Length];
            b[0] = (byte)Kind.SonyEffect;
            effectPayload.CopyTo(b.AsSpan(1));
            return b;
        }

        /// <summary>Decoded view of an output frame. Fails closed: a malformed frame
        /// decodes to <see cref="Kind"/> 0 and is ignored by the caller.</summary>
        public readonly struct OutputEffect
        {
            public readonly Kind Kind;
            public readonly ushort Left, Right, LeftTrigger, RightTrigger;
            public readonly byte[] Effect; // SonyEffect raw body, else null

            public OutputEffect(Kind kind, ushort left, ushort right, ushort lt, ushort rt, byte[] effect)
            {
                Kind = kind; Left = left; Right = right; LeftTrigger = lt; RightTrigger = rt; Effect = effect;
            }
        }

        /// <summary>Decode a payload. Returns false (and a default struct) on any
        /// malformed input — never throws, never trusts a short/oversized frame.</summary>
        public static bool TryDecode(ReadOnlySpan<byte> payload, out OutputEffect effect)
        {
            effect = default;
            if (payload.Length < 1) return false;
            var kind = (Kind)payload[0];
            try
            {
                switch (kind)
                {
                    case Kind.Rumble:
                        if (payload.Length < 9) return false;
                        effect = new OutputEffect(
                            Kind.Rumble,
                            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(1, 2)),
                            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(3, 2)),
                            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(5, 2)),
                            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(7, 2)),
                            null);
                        return true;
                    case Kind.SonyEffect:
                        if (payload.Length < 2 || payload.Length > 1 + 256) return false;
                        effect = new OutputEffect(Kind.SonyEffect, 0, 0, 0, 0, payload.Slice(1).ToArray());
                        return true;
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
    }
}
