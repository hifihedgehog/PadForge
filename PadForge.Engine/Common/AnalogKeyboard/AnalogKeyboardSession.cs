using System;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// The HID channel an analog keyboard is driven over (issue #468). The
    /// app implements it over overlapped handles, and tests over a scripted
    /// fake, so every route's request logic runs the same either way.
    /// </summary>
    public interface IAnalogKeyboardTransport
    {
        /// <summary>Writes one output report with WriteFile, report ID first.
        /// The transport pads it to the output report length, as Soup's
        /// sendReport does on Windows. False when the write failed.</summary>
        bool Send(byte[] report);

        /// <summary>Writes one output report with HidD_SetOutputReport, a
        /// control transfer, report ID first and padded the same way. For the
        /// firmware that ignores interrupt OUT writes.</summary>
        bool SendOutputReport(byte[] report);

        /// <summary>Reads one input report into <paramref name="buffer"/>,
        /// report ID first (0 when the collection has none), waiting up to
        /// <paramref name="timeoutMs"/>. Returns the byte count, 0 on a
        /// timeout, or -1 when the device is gone. A route with a companion
        /// collection reads from the companion.</summary>
        int Receive(byte[] buffer, int timeoutMs);

        /// <summary>Throws away reports that arrived before the next request,
        /// Soup's discardStaleReports.</summary>
        void DiscardStale();

        /// <summary>Writes a feature report (IOCTL_HID_SET_FEATURE), report
        /// ID first, padded to the feature report length.</summary>
        bool SetFeature(byte[] report);

        /// <summary>Reads a feature report (IOCTL_HID_GET_FEATURE) into
        /// <paramref name="buffer"/>, whose byte 0 holds the report ID on
        /// entry. Returns the byte count the driver reports, or -1.</summary>
        int GetFeature(byte[] buffer);

        /// <summary>Windows report lengths of the collection, report ID byte
        /// included.</summary>
        int InputLength { get; }
        int OutputLength { get; }
        int FeatureLength { get; }
    }

    /// <summary>Outcome of one pass.</summary>
    public enum AnalogPollResult
    {
        /// <summary>The pass produced a current key set.</summary>
        Ok,
        /// <summary>The keyboard did not answer in time.</summary>
        NoAnswer,
        /// <summary>The device is gone, or the session cannot go on.</summary>
        Failed,
        /// <summary>Nothing new arrived and nothing was asked: a pushed
        /// keyboard at rest. Neither a report nor a miss.</summary>
        Idle,
    }

    /// <summary>
    /// One route's conversation with one keyboard (issue #468). The device
    /// row calls <see cref="Start"/> once on the thread that opens the
    /// keyboard, before the row exists, then <see cref="Pass"/> back to back
    /// on its reader thread, then <see cref="Stop"/> once while the handle is
    /// still open. A pass leaves the current key depths in its output, which
    /// persists between passes, so a route that receives changes only (a
    /// pushed event stream) updates the keys it hears about and keeps the
    /// rest.
    /// </summary>
    public abstract class AnalogKeyboardSession
    {
        /// <summary>How long one answer may take. A USB keyboard answers
        /// within a few milliseconds, so this only matters when it does not
        /// answer at all.</summary>
        public const int AnswerTimeoutMs = 100;

        /// <summary>Scratch for received reports, sized for the largest
        /// report any route reads.</summary>
        protected readonly byte[] Buffer = new byte[2048];

        /// <summary>Proves the keyboard is this route's and prepares it:
        /// identity handshakes, key map reads, and any mode the keyboard must
        /// be in to report depth. False when the keyboard is not this route's
        /// or did not answer, and then the next route in line gets a turn.
        /// Runs before the device row exists, on the sweep's worker.</summary>
        public virtual bool Start(IAnalogKeyboardTransport io) => true;

        /// <summary>Runs one pass. <paramref name="isHeld"/> answers whether
        /// Windows sees a key down right now, which the routes that read a
        /// few keys per request use to read the pressed keys first.</summary>
        public abstract AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld);

        /// <summary>Undoes what <see cref="Start"/> changed on the keyboard,
        /// while the handle is still open. Best effort: the keyboard may
        /// already be gone.</summary>
        public virtual void Stop(IAnalogKeyboardTransport io) { }

        /// <summary>The exact model once <see cref="Start"/> knows it, or null
        /// to keep the catalog's or the product string's name.</summary>
        public virtual string ModelName => null;

        /// <summary>The keys the input picker lists, in keyboard order, or
        /// null for the catalog's list.</summary>
        public virtual int[] KeyOrder => null;

        /// <summary>True for the routes that report only while Razer Synapse
        /// runs. The row checks the process and says so on the status line.</summary>
        public virtual bool NeedsSynapse => false;

        /// <summary>Passes in a row that may go unanswered before the row is
        /// given up and the sweep's retry cooldown takes over.</summary>
        public virtual int MissLimit => 20;

        /// <summary>Set by a Start that failed after it wrote to the keyboard.
        /// The route's timed retry (<see cref="AnalogKeyboardRoute.StartRetryMs"/>)
        /// then does not apply, so a handshake that keeps failing never turns
        /// into a loop of writes.</summary>
        public bool NoStartRetry { get; protected set; }

        /// <summary>Reads until an answer whose first two data bytes are
        /// <paramref name="b0"/> and <paramref name="b1"/> arrives, skipping
        /// anything else, Soup's safeReceiveReport. Returns the data offset
        /// (1 past a leading zero report ID) or -1 on timeout, -2 when the
        /// device is gone.</summary>
        protected int ReceiveMatching(IAnalogKeyboardTransport io, byte b0, byte b1, out int length)
        {
            long deadline = Environment.TickCount64 + AnswerTimeoutMs;
            while (true)
            {
                int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
                int n = io.Receive(Buffer, remaining);
                length = n;
                if (n < 0) return -2;
                if (n == 0) return -1;
                int off = n > 0 && Buffer[0] == 0 ? 1 : 0;
                if (n >= off + 2 && Buffer[off] == b0 && Buffer[off + 1] == b1) return off;
                if (Environment.TickCount64 >= deadline) return -1;
            }
        }
    }

    /// <summary>
    /// The Soup families that push their state (Wooting, Razer): one pass
    /// waits for one input report and parses it. A keyboard at rest sends
    /// nothing, so a quiet wait is an ordinary pass, not a miss. A Razer
    /// collection may fail a read while Synapse switches the keyboard's mode
    /// without the keyboard leaving, so it is given up only after Soup's ten
    /// failures in a row. Every other family stops at the first.
    /// </summary>
    public sealed class PushedReportSession : AnalogKeyboardSession
    {
        /// <summary>Longest single wait, so the reader sees a stop request
        /// and the Synapse check stays current.</summary>
        public const int WaitMs = 250;

        private const int RazerReadErrorsTolerated = 10;

        private readonly AnalogKeyboardProtocol _protocol;
        private readonly ushort _vendorId;
        private readonly ushort _productId;
        private int _errors;

        public PushedReportSession(AnalogKeyboardProtocol protocol, ushort vendorId, ushort productId)
        {
            _protocol = protocol;
            _vendorId = vendorId;
            _productId = productId;
        }

        public override bool NeedsSynapse => AnalogKeyboardCatalog.NeedsSynapse(_protocol);

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            int n = io.Receive(Buffer, WaitMs);
            if (n < 0)
            {
                int tolerated = NeedsSynapse ? RazerReadErrorsTolerated : 1;
                if (++_errors >= tolerated) return AnalogPollResult.Failed;
                System.Threading.Thread.Sleep(50);
                return AnalogPollResult.Idle;
            }
            _errors = 0;
            if (n == 0) return AnalogPollResult.Idle;
            return AnalogKeyboardParsers.ParsePushed(_protocol, Buffer.AsSpan(0, n), output, _vendorId, _productId)
                ? AnalogPollResult.Ok
                : AnalogPollResult.Idle;
        }
    }
}
