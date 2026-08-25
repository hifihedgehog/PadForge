using System;
using System.Collections.Generic;

namespace PadForge.Engine.Common
{
    /// <summary>How a learned vendor-report button reads its state.</summary>
    public enum VendorButtonKind
    {
        /// <summary>The bits under <see cref="VendorButtonDefinition.Mask"/> at
        /// <see cref="VendorButtonDefinition.ByteIndex"/> read
        /// <see cref="VendorButtonDefinition.Value"/> while pressed and flip
        /// back on release. Active-high on every documented handheld
        /// (Legion Go byte 20, GPD Win 5 bytes 7 to 9, Zotac wheels), but
        /// the pressed pattern is stored, so an active-low bit learns as
        /// itself instead of inverted.</summary>
        Bit = 0,
        /// <summary>The byte equals <see cref="VendorButtonDefinition.Value"/>
        /// while pressed (reports that carry a key code per event, the ROG
        /// Ally 0x5A shape). Released when the byte reads anything else or
        /// when the report stops arriving for <see cref="VendorReportLearner.ValueHoldMs"/>.</summary>
        Value = 1,
    }

    /// <summary>One learned button in a vendor HID input report (issue #343):
    /// which report it lives in, which byte, and how to read it.</summary>
    public sealed class VendorButtonDefinition
    {
        public string Name { get; set; }
        /// <summary>STABLE raw-button index on the vendor device row.</summary>
        public int Button { get; set; }
        /// <summary>Report id of the input report, 0 when the collection has none.</summary>
        public byte ReportId { get; set; }
        /// <summary>Byte offset INCLUDING the report id byte at index 0, so a
        /// definition is stable against how the buffer was captured.</summary>
        public int ByteIndex { get; set; }
        public byte Mask { get; set; }
        public byte Value { get; set; }
        public VendorButtonKind Kind { get; set; }

        /// <summary>Reads the button from a full report buffer.</summary>
        public bool Evaluate(ReadOnlySpan<byte> report)
        {
            if (ByteIndex < 0 || ByteIndex >= report.Length) return false;
            if (ReportId != 0 && report[0] != ReportId) return false;
            byte b = report[ByteIndex];
            return Kind == VendorButtonKind.Bit ? Mask != 0 && (b & Mask) == (Value & Mask) : b == Value;
        }
    }

    /// <summary>A candidate the learner found in the press/release diff.</summary>
    public readonly struct VendorButtonCandidate
    {
        public readonly int ByteIndex;
        public readonly byte Mask;
        public readonly byte Value;
        public readonly VendorButtonKind Kind;
        public VendorButtonCandidate(int byteIndex, byte mask, byte value, VendorButtonKind kind)
        { ByteIndex = byteIndex; Mask = mask; Value = value; Kind = kind; }
        public override string ToString() => Kind == VendorButtonKind.Bit
            ? $"byte {ByteIndex} bit 0x{Mask:X2}{(Value == 0 ? " clear" : "")}"
            : $"byte {ByteIndex} == 0x{Value:X2}";
    }

    /// <summary>
    /// The press-it-to-learn-it core for vendor HID reports (issue #343).
    /// Pure functions over captured report buffers, so the whole contract is
    /// replay-testable from a byte capture without the hardware.
    ///
    /// <para>Three phases. <see cref="NoiseMask"/> marks every bit that
    /// changed on its own while the device was idle (IMU words, counters,
    /// timestamps), so they can never become a button. <see cref="FindBits"/>
    /// diffs the press against the idle baseline outside that mask and keeps
    /// only bits that also flip back on release. <see cref="FindValues"/>
    /// covers reports that carry a value byte per event rather than a state
    /// bit: a byte outside the mask that reads one constant during the press
    /// and something else after it.</para>
    /// </summary>
    public static class VendorReportLearner
    {
        /// <summary>A Value-kind button holds after its last matching report
        /// for this long, since event-style firmware may not send a release.</summary>
        public const int ValueHoldMs = 150;

        /// <summary>Bit-level volatility mask: bit set where any idle sample
        /// disagreed with the first. Reports of unequal length are compared
        /// over the shorter prefix, with the tail treated as volatile.</summary>
        public static byte[] NoiseMask(IReadOnlyList<byte[]> idleSamples)
        {
            if (idleSamples == null || idleSamples.Count == 0) return Array.Empty<byte>();
            int len = idleSamples[0].Length;
            var mask = new byte[len];
            for (int s = 1; s < idleSamples.Count; s++)
            {
                var cur = idleSamples[s];
                int n = Math.Min(len, cur.Length);
                for (int i = 0; i < n; i++)
                    mask[i] |= (byte)(idleSamples[0][i] ^ cur[i]);
                for (int i = n; i < len; i++) mask[i] = 0xFF;
            }
            return mask;
        }

        /// <summary>Consecutive flipped samples a press must show: 2.5% of
        /// the window (about 75 ms of a 3 s window at any report rate),
        /// never fewer than two when two exist. A single captured sample
        /// (event-style firmware that reports once) is its own run.</summary>
        public static int MinPressRun(int pressSamples) => Math.Max(Math.Min(2, pressSamples), pressSamples / 40);

        /// <summary>Bit candidates: outside the noise mask, the bit reads the
        /// opposite of the idle baseline in at least one press sample and is
        /// back at baseline by the last sample seen (the last release sample,
        /// or the last press sample when nothing arrived after release). A
        /// hold and a tap both qualify: a tap inside the press window leaves
        /// pressed samples followed by released ones, and the noise mask
        /// already rules out anything that flips on its own. One candidate
        /// per byte carrying the combined mask of qualifying bits.</summary>
        public static List<VendorButtonCandidate> FindBits(byte[] idle, byte[] noise,
            IReadOnlyList<byte[]> press, IReadOnlyList<byte[]> release)
        {
            var result = new List<VendorButtonCandidate>();
            if (idle == null || press == null || press.Count == 0) return result;
            int len = idle.Length;
            byte[] last = release != null && release.Count > 0 ? release[release.Count - 1] : press[press.Count - 1];
            for (int i = 0; i < len; i++)
            {
                byte volatileBits = noise != null && i < noise.Length ? noise[i] : (byte)0;
                byte candidate = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    byte m = (byte)(1 << bit);
                    if ((volatileBits & m) != 0) continue;
                    byte idleVal = (byte)(idle[i] & m);
                    // A press is a RUN of flipped samples, not one: a tap
                    // holds the bit for tens of milliseconds, while a motion
                    // byte at rest wiggles a bit for a sample and lets go.
                    int run = 0, best = 0;
                    foreach (var p in press)
                    {
                        bool f = i < p.Length && (p[i] & m) != idleVal;
                        run = f ? run + 1 : 0;
                        if (run > best) best = run;
                    }
                    if (best < MinPressRun(press.Count)) continue;
                    // Back at rest by the end: a bit that stays flipped is a
                    // mode or toggle, not a button.
                    if (i >= last.Length || (last[i] & m) != idleVal) continue;
                    candidate |= m;
                }
                // Value carries the PRESSED pattern under the mask, so a bit
                // that clears on press (active-low) evaluates as pressed
                // when clear.
                // The pressed pattern is idle inverted under the mask.
                if (candidate != 0)
                    result.Add(new VendorButtonCandidate(i, candidate, (byte)(~idle[i] & candidate), VendorButtonKind.Bit));
            }
            return result;
        }

        /// <summary>Value candidates: a byte outside the noise mask whose
        /// first press sample that differs from idle carries a value no
        /// release sample carries. Later press samples may differ (a tap's
        /// own release code lands inside the press window on event-style
        /// firmware); a value still present after release is a mode byte,
        /// not a button. Bytes already explained by a single flipped bit are
        /// left to <see cref="FindBits"/>.</summary>
        public static List<VendorButtonCandidate> FindValues(byte[] idle, byte[] noise,
            IReadOnlyList<byte[]> press, IReadOnlyList<byte[]> release)
        {
            var result = new List<VendorButtonCandidate>();
            if (idle == null || press == null || press.Count == 0) return result;
            int len = idle.Length;
            for (int i = 0; i < len; i++)
            {
                if (noise != null && i < noise.Length && noise[i] != 0) continue;
                byte v = idle[i];
                bool found = false;
                foreach (var p in press)
                    if (i < p.Length && p[i] != idle[i]) { v = p[i]; found = true; break; }
                if (!found) continue;
                if (release != null)
                {
                    bool seenOnRelease = false;
                    foreach (var r in release)
                        if (i < r.Length && r[i] == v) { seenOnRelease = true; break; }
                    if (seenOnRelease) continue;
                }
                // A single-bit difference is a Bit candidate, not a value.
                int diff = v ^ idle[i];
                if ((diff & (diff - 1)) == 0) continue;
                result.Add(new VendorButtonCandidate(i, 0, v, VendorButtonKind.Value));
            }
            return result;
        }

        /// <summary>Runs both finders and prefers a single Bit candidate,
        /// then a single Value candidate. More than one candidate of a kind
        /// means the press changed several things at once (two buttons, or
        /// a byte plus its mirror); the caller shows them and lets the user
        /// pick, or presses again.</summary>
        public static List<VendorButtonCandidate> Learn(byte[] idle, byte[] noise,
            IReadOnlyList<byte[]> press, IReadOnlyList<byte[]> release)
        {
            var bits = FindBits(idle, noise, press, release);
            if (bits.Count > 0)
            {
                // A byte whose press flipped MORE than one bit is a code, not
                // a flag: the ROG Ally writes 166 for one button and 167 for
                // its neighbour, and a mask match on 0xA6 would fire on both.
                // Exact equality is the stricter reading and still holds for
                // a flag byte whose flags rise together (GPD Win 5's 0x69).
                for (int i = 0; i < bits.Count; i++)
                {
                    var c = bits[i];
                    int m = c.Mask;
                    if ((m & (m - 1)) == 0) continue;
                    byte code = idle[c.ByteIndex];
                    foreach (var p in press)
                        if (c.ByteIndex < p.Length && p[c.ByteIndex] != idle[c.ByteIndex]) { code = p[c.ByteIndex]; break; }
                    bits[i] = new VendorButtonCandidate(c.ByteIndex, 0, code, VendorButtonKind.Value);
                }
                return bits;
            }
            return FindValues(idle, noise, press, release);
        }
    }
}
