using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PadForge.Engine.Recording;

namespace PadForge.Common.Recording
{
    /// <summary>One XInput place's state as a game reads it.</summary>
    internal readonly record struct XInputRead(bool Connected, ushort Buttons, byte LeftTrigger, byte RightTrigger,
        short LeftX, short LeftY, short RightX, short RightY);

    /// <summary>
    /// What a game reads back through XInput (#600), read from the XInput
    /// library PadForge itself loads (xinput1_4.dll, which may be PadForge's
    /// own OpenXInput build or Windows'), at the four places a game reads.
    /// The recording writes a place's state when it changes, and which
    /// virtual controller a place belongs to once a frame PadForge handed
    /// over is read back there.
    ///
    /// <para>The test host reads no controller of the machine's: with
    /// <see cref="OffLimitsForTest"/> set, a place comes from
    /// <see cref="ReadSeam"/> or not at all.</para>
    /// </summary>
    internal static class XInputReadBack
    {
        /// <summary>The places a game reads.</summary>
        internal const int Places = 4;

        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: a place's state.</summary>
        internal static Func<int, XInputRead> ReadSeam;

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint PacketNumber;
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint userIndex, out XInputState state);

        private static bool _missing;

        /// <summary>A place's state, or one not connected.</summary>
        internal static XInputRead Read(int place)
        {
            if (OffLimitsForTest && ReadSeam == null) return default;
            var seam = ReadSeam;
            if (seam != null) return seam(place);
            if (_missing || (uint)place >= Places) return default;
            try
            {
                if (XInputGetState((uint)place, out var s) != 0) return default;
                return new XInputRead(true, s.Buttons, s.LeftTrigger, s.RightTrigger, s.ThumbLX, s.ThumbLY, s.ThumbRX, s.ThumbRY);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _missing = true;
                return default;
            }
        }

        /// <summary>A place's state as the tokens of its line: the buttons by
        /// name, then each stick and trigger, exact.</summary>
        internal static string Tokens(XInputRead read)
        {
            if (!read.Connected) return "none";
            var sb = new StringBuilder(96);
            sb.Append("Buttons=");
            RecordingControlNames.AppendXInputButtons(sb, read.Buttons);
            sb.Append(" LeftTrigger=").Append(read.LeftTrigger.ToString(CultureInfo.InvariantCulture))
              .Append(" RightTrigger=").Append(read.RightTrigger.ToString(CultureInfo.InvariantCulture))
              .Append(" LeftX=").Append(read.LeftX.ToString(CultureInfo.InvariantCulture))
              .Append(" LeftY=").Append(read.LeftY.ToString(CultureInfo.InvariantCulture))
              .Append(" RightX=").Append(read.RightX.ToString(CultureInfo.InvariantCulture))
              .Append(" RightY=").Append(read.RightY.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>Whether a place reads the frame a virtual controller was
        /// handed: the same buttons and sticks, and triggers at the byte the
        /// handed value packs to. A frame at rest matches every idle place,
        /// so it names none.</summary>
        internal static bool Matches(XInputRead read, RecordedOutputState handed)
        {
            if (!read.Connected || handed == null) return false;
            bool rest = handed.Buttons == 0 && handed.LeftX == 0 && handed.LeftY == 0 && handed.RightX == 0 && handed.RightY == 0
                && handed.LeftTrigger == 0 && handed.RightTrigger == 0;
            if (rest) return false;
            return read.Buttons == handed.Buttons && read.LeftX == handed.LeftX && read.LeftY == handed.LeftY
                && read.RightX == handed.RightX && read.RightY == handed.RightY
                && Math.Abs(read.LeftTrigger - (handed.LeftTrigger >> 8)) <= 1 && Math.Abs(read.RightTrigger - (handed.RightTrigger >> 8)) <= 1;
        }

        /// <summary>Which XInput library this process has loaded, and whose:
        /// PadForge's own copy beside it or Windows'. Only file names.</summary>
        internal static string LibraryLine()
        {
            IntPtr module = GetModuleHandle("xinput1_4.dll");
            if (module == IntPtr.Zero) return "XInput read back with: xinput1_4.dll, not loaded yet";
            var path = new StringBuilder(520);
            int n = GetModuleFileName(module, path, path.Capacity);
            string file = n > 0 ? path.ToString(0, n) : string.Empty;
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            bool windows = file.Length > 0 && string.Equals(Path.GetDirectoryName(file), system, StringComparison.OrdinalIgnoreCase);
            return "XInput read back with: " + (file.Length == 0 ? "xinput1_4.dll" : Path.GetFileName(file)) + ", " + (windows ? "Windows' own" : "PadForge's own");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetModuleFileName(IntPtr module, StringBuilder path, int size);
    }
}
