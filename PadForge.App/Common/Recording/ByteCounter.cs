using System;
using System.IO;

namespace PadForge.Common.Recording
{
    /// <summary>
    /// A stream that keeps nothing and counts what is written to it. A
    /// recording packs its text into one, so the 20 MB limit reads the size
    /// the zip will have while the plain text goes to the file (#597).
    /// </summary>
    internal sealed class ByteCounter : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    }
}
