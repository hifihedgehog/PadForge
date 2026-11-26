using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace PadForge.Engine.Common.NativeHost
{
    /// <summary>A library the host serves: one channel's requests, on the
    /// channel's own thread, until it ends.</summary>
    public interface IHostService
    {
        void Serve(HostConnection channel);
    }

    /// <summary>Opens a channel to a service of the library host, or answers
    /// null with why not.</summary>
    public delegate HostConnection HostOpener(string service, out string reason);

    /// <summary>
    /// One channel between PadForge and its library host (#600): a pipe
    /// that carries framed messages, each a four-byte little-endian length
    /// and that many bytes, at most <see cref="MaxFrame"/>. PadForge's side
    /// calls (<see cref="Call"/>): it writes one request and reads one
    /// answer, one call at a time. The host's side serves
    /// (<see cref="Receive"/>, <see cref="Send"/>) on a thread of its own.
    ///
    /// <para>A broken pipe, a frame past its bound or an answer that breaks
    /// the protocol ends the channel for good: every later call answers
    /// false, and the caller acts as on a failed call of the library behind
    /// it.</para>
    /// </summary>
    public sealed class HostConnection : IDisposable
    {
        /// <summary>The largest frame either side takes.</summary>
        public const int MaxFrame = 1 << 20;

        private readonly Stream _in, _out;
        private readonly IDisposable _owner;
        private readonly object _gate = new();
        private readonly MessageWriter _writer = new();
        private readonly MessageReader _reader = new();
        private byte[] _frame = new byte[512];
        private readonly byte[] _header = new byte[MessageWriter.HeaderSize];
        private volatile bool _broken;
        private long _callStarted;

        public HostConnection(Stream input, Stream output, IDisposable owner = null)
        {
            _in = input ?? throw new ArgumentNullException(nameof(input));
            _out = output ?? throw new ArgumentNullException(nameof(output));
            _owner = owner;
        }

        /// <summary>The host generation this channel belongs to.</summary>
        public int Generation { get; init; }

        /// <summary>Whether the channel ended.</summary>
        public bool Broken => _broken;

        /// <summary>The Stopwatch tick a call in progress started at, or zero
        /// with none, for the host's watchdog.</summary>
        public long CallStarted => System.Threading.Interlocked.Read(ref _callStarted);

        /// <summary>
        /// Writes the request <paramref name="request"/> fills and reads the
        /// answer into <paramref name="answer"/>. False when the channel
        /// ended, now or before, or the answer broke the protocol, which ends
        /// it.
        /// </summary>
        public bool Call(Action<MessageWriter> request, Action<MessageReader> answer)
        {
            lock (_gate)
            {
                if (_broken) return false;
                System.Threading.Interlocked.Exchange(ref _callStarted, Stopwatch.GetTimestamp());
                try
                {
                    request(_writer.Reset());
                    WriteFrame(_writer);
                    if (!ReadFrame(out int length)) return Break();
                    _reader.Reset(_frame, length);
                    answer?.Invoke(_reader);
                    _reader.End();
                    return true;
                }
                catch (Exception)
                {
                    // An IOException from a pipe the host closed, or a
                    // HostProtocolException from what it sent.
                    return Break();
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _callStarted, 0);
                }
            }
        }

        /// <summary>The host's side: the next request, or false when the
        /// channel ended.</summary>
        public bool Receive(out MessageReader request)
        {
            request = null;
            if (_broken) return false;
            try
            {
                if (!ReadFrame(out int length)) return Break();
                _reader.Reset(_frame, length);
                request = _reader;
                return true;
            }
            catch (Exception)
            {
                return Break();
            }
        }

        /// <summary>The host's side: sends the answer <paramref name="answer"/>
        /// fills. False when the channel ended.</summary>
        public bool Send(Action<MessageWriter> answer)
        {
            if (_broken) return false;
            try
            {
                answer(_writer.Reset());
                WriteFrame(_writer);
                return true;
            }
            catch (Exception)
            {
                return Break();
            }
        }

        private void WriteFrame(MessageWriter message)
        {
            BinaryPrimitives.WriteInt32LittleEndian(message.Buffer.AsSpan(0, MessageWriter.HeaderSize), message.Length - MessageWriter.HeaderSize);
            _out.Write(message.Buffer, 0, message.Length);
            _out.Flush();
        }

        private bool ReadFrame(out int length)
        {
            length = 0;
            if (!ReadExactly(_header, MessageWriter.HeaderSize)) return false;
            length = BinaryPrimitives.ReadInt32LittleEndian(_header);
            if (length < 0 || length > MaxFrame) throw new HostProtocolException("frame out of bounds");
            if (_frame.Length < length) Array.Resize(ref _frame, Math.Max(length, _frame.Length * 2));
            return ReadExactly(_frame, length);
        }

        private bool ReadExactly(byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = _in.Read(buffer, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private bool Break()
        {
            _broken = true;
            try { _owner?.Dispose(); } catch (Exception) { }
            return false;
        }

        public void Dispose() => Break();
    }
}
