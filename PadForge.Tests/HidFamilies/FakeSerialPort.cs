using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>A USB serial port, scripted: it logs every write and hands a
    /// read what the test queued, a few bytes at a time.</summary>
    internal sealed class FakeSerialPort : ISerialLineChannel
    {
        public readonly List<byte[]> Writes = new();
        public readonly Queue<byte> Pending = new();
        public int Reads;
        public bool FailWrites;
        public bool FailReads;
        public bool Disposed;
        public int Chunk = 4;

        /// <summary>What the device answers a write with, or null.</summary>
        public Func<byte[], byte[]> Answer = _ => null;

        public bool Write(byte[] bytes)
        {
            if (FailWrites) return false;
            lock (Writes) Writes.Add((byte[])bytes.Clone());
            var answer = Answer(bytes);
            if (answer != null) foreach (byte b in answer) Pending.Enqueue(b);
            return true;
        }

        public int Read(byte[] buffer)
        {
            Reads++;
            if (FailReads) return -1;
            int n = 0;
            while (n < buffer.Length && n < Chunk && Pending.Count > 0) buffer[n++] = Pending.Dequeue();
            return n;
        }

        public List<byte[]> Written()
        {
            lock (Writes) return Writes.ToList();
        }

        public void Dispose() => Disposed = true;
    }
}
