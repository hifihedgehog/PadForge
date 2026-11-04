using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using PadForge.Engine;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>A channel that records what a session sends and answers
    /// input reads and feature reads from queues the test fills.</summary>
    internal sealed class RazerScriptChannel : IHidReportChannel
    {
        public readonly List<string> Log = new();
        public readonly List<byte[]> Writes = new();

        /// <summary>The answer to each input read, in turn, after a write
        /// whose bytes the key matches. A read with none answers 0.</summary>
        public Func<byte[], byte[]> AnswerTo = _ => null;
        private byte[] _pending;

        public Func<byte[], byte[]> FeatureReply = _ => null;
        public Func<byte[], bool> Fails = _ => false;

        public string Path { get; init; } = @"\\?\hid#vid_1532&pid_0000&mi_00#test";
        public bool FeatureOnly => false;
        public int InputReportLength { get; init; } = 37;
        public int OutputReportLength { get; init; } = 65;
        public int FeatureReportLength { get; init; } = 91;

        public bool SetFeature(byte[] report)
        {
            if (Fails(report)) return false;
            Log.Add("F " + Hex(report, 16));
            Writes.Add((byte[])report.Clone());
            return true;
        }

        public bool GetFeature(byte reportId, byte[] buffer)
        {
            Log.Add("G " + reportId.ToString("X2"));
            var answer = FeatureReply(Writes.LastOrDefault());
            if (answer == null) return false;
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return true;
        }

        public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
        {
            if (Fails(report)) return false;
            Log.Add("O " + Hex(report, 16));
            Writes.Add((byte[])report.Clone());
            _pending = AnswerTo(report);
            return true;
        }

        public int ReadInput(byte[] buffer, int timeoutMs)
        {
            Log.Add("R " + timeoutMs);
            var answer = _pending;
            _pending = null;
            if (answer == null) return 0;
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return Math.Min(answer.Length, buffer.Length);
        }

        public void Wait(int ms) => Log.Add("W " + ms);

        public void Dispose() { }
    }
    /// <summary>The 90-byte reports as OpenRGB builds them, the checksum the
    /// XOR of bytes 3 to 88 (RazerController.cpp:602-623 and 629-651).</summary>
    internal static class RazerReport90
    {
        public static string Of(byte transaction, byte cls, byte cmd, byte size, params byte[] arguments)
        {
            var b = new byte[91];
            b[2] = transaction;
            b[6] = size;
            b[7] = cls;
            b[8] = cmd;
            Array.Copy(arguments, 0, b, 9, arguments.Length);
            byte crc = 0;
            for (int i = 3; i < 89; i++) crc ^= b[i];
            b[89] = crc;
            return Hex(b);
        }

        public static byte[] Reply(byte[] request, params (int At, byte Value)[] arguments)
        {
            var reply = (byte[])request.Clone();
            reply[1] = 0x02;
            foreach (var (at, value) in arguments) reply[9 + at] = value;
            return reply;
        }
    }
}
