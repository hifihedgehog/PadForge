using System;
using System.Buffers.Binary;
using System.IO;

namespace PadForge.Engine.Common.NativeHost
{
    /// <summary>A message that broke the protocol: a size, a count or a
    /// value out of its bounds, or a frame cut short. The side that reads one
    /// drops the channel.</summary>
    public sealed class HostProtocolException : Exception
    {
        public HostProtocolException(string message) : base(message) { }
    }

    /// <summary>
    /// Writes one message of the host's protocol (#600): little-endian
    /// numbers and length-prefixed UTF-16 text, after four bytes the
    /// connection fills with the length. One writer is reused for every
    /// message of a connection, so a message's bytes take no new buffer once
    /// the writer has grown to fit them. The delegates a call passes are the
    /// caller's to allocate.
    /// </summary>
    public sealed class MessageWriter
    {
        public const int HeaderSize = 4;
        private byte[] _buffer = new byte[256];
        private int _length = HeaderSize;

        public byte[] Buffer => _buffer;

        /// <summary>The message's length, its header included.</summary>
        public int Length => _length;

        public MessageWriter Reset()
        {
            _length = HeaderSize;
            return this;
        }

        private Span<byte> Take(int count)
        {
            if (_length + count > _buffer.Length)
            {
                if (_length + count > HostConnection.MaxFrame + HeaderSize) throw new HostProtocolException("message too long");
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
            }
            var span = _buffer.AsSpan(_length, count);
            _length += count;
            return span;
        }

        public MessageWriter Int(int value) { BinaryPrimitives.WriteInt32LittleEndian(Take(4), value); return this; }
        public MessageWriter UInt(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value); return this; }
        public MessageWriter Long(long value) { BinaryPrimitives.WriteInt64LittleEndian(Take(8), value); return this; }
        public MessageWriter ULong(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(Take(8), value); return this; }
        public MessageWriter Double(double value) { BinaryPrimitives.WriteDoubleLittleEndian(Take(8), value); return this; }
        public MessageWriter Float(float value) { BinaryPrimitives.WriteSingleLittleEndian(Take(4), value); return this; }
        public MessageWriter Bool(bool value) { Take(1)[0] = value ? (byte)1 : (byte)0; return this; }
        public MessageWriter Byte(byte value) { Take(1)[0] = value; return this; }

        /// <summary>Text as its length in characters, -1 for null, then its
        /// UTF-16 code units.</summary>
        public MessageWriter Text(string value)
        {
            if (value == null) return Int(-1);
            Int(value.Length);
            var span = Take(value.Length * 2);
            for (int i = 0; i < value.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * 2, 2), value[i]);
            return this;
        }

        public MessageWriter Bytes(ReadOnlySpan<byte> value)
        {
            Int(value.Length);
            value.CopyTo(Take(value.Length));
            return this;
        }
    }

    /// <summary>
    /// Reads one message of the host's protocol. Every read is bounded: a
    /// read past the message's end, a count or a length past what the caller
    /// allows, and text with a negative length other than -1 throw
    /// <see cref="HostProtocolException"/>. What the numbers mean is the
    /// caller's to bound.
    /// </summary>
    public sealed class MessageReader
    {
        private byte[] _buffer;
        private int _at, _end;

        public void Reset(byte[] buffer, int length)
        {
            _buffer = buffer;
            _at = 0;
            _end = length;
        }

        /// <summary>Whether the whole message was read.</summary>
        public bool AtEnd => _at == _end;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _end - _at < count) throw new HostProtocolException("message cut short");
            var span = _buffer.AsSpan(_at, count);
            _at += count;
            return span;
        }

        public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public uint UInt() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public long Long() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public ulong ULong() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public double Double() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
        public float Float() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public byte Byte() => Take(1)[0];

        public bool Bool()
        {
            byte b = Byte();
            if (b > 1) throw new HostProtocolException("not a truth value");
            return b == 1;
        }

        /// <summary>An int within [<paramref name="min"/>, <paramref name="max"/>].</summary>
        public int Int(int min, int max)
        {
            int value = Int();
            if (value < min || value > max) throw new HostProtocolException("number out of bounds");
            return value;
        }

        /// <summary>A finite double within [<paramref name="min"/>, <paramref name="max"/>].</summary>
        public double Double(double min, double max)
        {
            double value = Double();
            if (!double.IsFinite(value) || value < min || value > max) throw new HostProtocolException("number out of bounds");
            return value;
        }

        /// <summary>A finite float within [<paramref name="min"/>, <paramref name="max"/>].</summary>
        public float Float(float min, float max)
        {
            float value = Float();
            if (!float.IsFinite(value) || value < min || value > max) throw new HostProtocolException("number out of bounds");
            return value;
        }

        /// <summary>Text of at most <paramref name="maxChars"/> characters,
        /// or null.</summary>
        public string Text(int maxChars)
        {
            int length = Int();
            if (length == -1) return null;
            if (length < 0 || length > maxChars) throw new HostProtocolException("text too long");
            var span = Take(length * 2);
            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * 2, 2));
            return new string(chars);
        }

        public byte[] Bytes(int maxLength)
        {
            int length = Int();
            if (length < 0 || length > maxLength) throw new HostProtocolException("data too long");
            return Take(length).ToArray();
        }

        /// <summary>Throws when anything is left unread: a message carries
        /// exactly what its kind says.</summary>
        public void End()
        {
            if (!AtEnd) throw new HostProtocolException("message longer than its kind");
        }
    }
}
