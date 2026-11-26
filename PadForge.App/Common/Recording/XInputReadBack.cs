using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PadForge.Engine.Recording;

namespace PadForge.Common.Recording
{
    /// <summary>One XInput number's state as PadForge's own XInput library
    /// reads it.</summary>
    internal readonly record struct XInputRead(bool Connected, ushort Buttons, byte LeftTrigger, byte RightTrigger,
        short LeftX, short LeftY, short RightX, short RightY);

    /// <summary>
    /// What PadForge's own XInput library reads (#600), at the first four
    /// numbers, the ones PadForge's reader uses: SDL reads through the same
    /// library, and its "XInput#N" paths carry these numbers. The recording
    /// writes a number's state when it changes, and the reader comparison
    /// compares a pad at a number with this read of the same number.
    ///
    /// <para>In the app that library is PadForge's OpenXInput fork, loaded
    /// by name from the folder the host unpacked into (App.xaml.cs). It gives
    /// no HIDMaestro controller a number (OpenXinput.cpp,
    /// IsHidMaestroInterface), so its numbers are not the ones a game reads
    /// once a virtual controller is connected, and no read here finds a
    /// virtual controller. The numbers a game reads are not read: Windows'
    /// own xinput1_4.dll would number PadForge's pads, which a game does
    /// not see behind HidHide, and write their player lights.</para>
    ///
    /// <para>The test host reads no controller of the machine's: with
    /// <see cref="OffLimitsForTest"/> set, a number comes from
    /// <see cref="ReadSeam"/> or not at all.</para>
    /// </summary>
    internal static class XInputReadBack
    {
        /// <summary>The numbers read.</summary>
        internal const int Places = 4;

        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: a number's state.</summary>
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

        /// <summary>A number's state, or one not connected.</summary>
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

        /// <summary>A number's state as the tokens of its line: the buttons
        /// by name, then each stick and trigger, exact.</summary>
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

        /// <summary>Which XInput library this process has loaded, and whose:
        /// PadForge's own copy or Windows'. Only file names.</summary>
        internal static string LibraryLine()
        {
            IntPtr module = GetModuleHandle("xinput1_4.dll");
            if (module == IntPtr.Zero) return LibraryLine(null, Environment.GetFolderPath(Environment.SpecialFolder.System));
            var path = new StringBuilder(520);
            int n = GetModuleFileName(module, path, path.Capacity);
            return LibraryLine(n > 0 ? path.ToString(0, n) : string.Empty, Environment.GetFolderPath(Environment.SpecialFolder.System));
        }

        /// <summary>The facts line for the library loaded from
        /// <paramref name="file"/>, null when none is loaded: whose it is, the
        /// numbering it reads by, and that the numbers a game reads are not
        /// read.</summary>
        internal static string LibraryLine(string file, string systemDirectory)
        {
            const string notRead = "The numbers a game reads are not read";
            if (file == null) return "XInput read back with: xinput1_4.dll, not loaded yet. " + notRead;
            bool windows = file.Length > 0 && string.Equals(Path.GetDirectoryName(file), systemDirectory, StringComparison.OrdinalIgnoreCase);
            return "XInput read back with: " + (file.Length == 0 ? "xinput1_4.dll" : Path.GetFileName(file))
                + (windows ? ", Windows' own" : ", PadForge's own") + ", by the numbers PadForge's reader uses. "
                + (windows ? "" : "It gives no virtual controller a number. ") + notRead;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetModuleFileName(IntPtr module, StringBuilder path, int size);
    }
}
