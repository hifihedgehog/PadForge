using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PadForge.Engine
{
    /// <summary>
    /// Which device serves an SDL XInput number. SDL's XInput backend names a
    /// pad only by its number: the path is "XInput#N", where N is the XInput
    /// user index, and it reports no serial (SDL_xinputjoystick.c,
    /// AddXInputDevice). OpenXInput hands out numbers in the order pads
    /// connect (OpenXinput.cpp, ProcessEnumeratedDevice). A pad on a number
    /// keeps it, and a new pad takes the number its player light shows when
    /// that number is free, or else the first free one.
    ///
    /// <para>PadForge ships its own OpenXInput as xinput1_4.dll, and SDL
    /// loads it (App.xaml.cs, SetDllDirectory). Its identity call names the
    /// XUSB interface that serves a number, with a generation that changes
    /// when another device takes the number (OpenXInputGetDeviceIdentityV1,
    /// OPENXINPUT_DEVICE_IDENTITY_V1 in OpenXinput.h). The call does no
    /// enumeration and no device I/O, and answers ERROR_BUSY rather than wait
    /// for its lock, which every XInput call holds during its device I/O
    /// (OpenXinput.cpp, ProcessAPIRequest). Microsoft's xinput1_4.dll has no
    /// such export. Windows names the device behind an interface
    /// (DEVPKEY_Device_InstanceId, devpkey.h).</para>
    ///
    /// <para>Output and hiding use it to reach the pad on a card's number
    /// (#583). A card's identity does not: pads of one model follow the order
    /// they connect, by the owner's decision of 2026-07-25 (e002074ed).</para>
    ///
    /// <para>The test host never calls the export: loading xinput1_4.dll
    /// there runs a DllMain that is not safe once other threads are running
    /// (App.xaml.cs). A test answers through <see cref="AnswerForTest"/>.</para>
    /// </summary>
    public static class XInputSlotIdentity
    {
        /// <summary>What a number's answer says.</summary>
        public enum Kind
        {
            /// <summary>The number is served by the interface named.</summary>
            Found,
            /// <summary>OpenXInput was busy. A later ask can answer.</summary>
            Busy,
            /// <summary>No device on the number now: empty, not ready yet, or
            /// a record OpenXInput could not read.</summary>
            None,
            /// <summary>No identity call here: Microsoft's xinput1_4.dll, an
            /// OpenXInput state override, or the test host. Final.</summary>
            Unsupported,
        }

        /// <summary>One number's answer: its kind, and for
        /// <see cref="Kind.Found"/> the interface, the generation and how
        /// many numbers the interface serves.</summary>
        public readonly struct Answer
        {
            public Answer(Kind kind, string interfacePath = null, ulong generation = 0, uint interfaceCount = 1)
            {
                Kind = kind;
                InterfacePath = kind == Kind.Found ? interfacePath : null;
                Generation = kind == Kind.Found ? generation : 0;
                InterfaceCount = kind == Kind.Found ? interfaceCount : 0;
            }

            public Kind Kind { get; }
            public string InterfacePath { get; }
            public ulong Generation { get; }

            /// <summary>The channels the interface reported when OpenXInput
            /// opened it. An interface with several serves several numbers,
            /// so its device names no single pad.</summary>
            public uint InterfaceCount { get; }
        }

        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: answers in place of the identity call when set.
        /// Per thread, so a test that sets it changes no other test's
        /// answer.</summary>
        [ThreadStatic]
        internal static Func<int, Answer> AnswerForTest;

        /// <summary>Test seam: answers in place of Windows for the device
        /// behind an interface. Per thread, as above.</summary>
        [ThreadStatic]
        internal static Func<string, string> InstanceIdForTest;

        /// <summary>The numbers OpenXInput serves: the fork is built with
        /// OPENXINPUT_XUSER_MAX_COUNT=16 (BluetoothLinkHelper.XInputSlotCount).</summary>
        public const int SlotCount = 16;

        /// <summary>Parses SDL's XInput path, "XInput#N" with N the number and
        /// nothing after it.</summary>
        public static bool TryParseSlot(string devicePath, out int slot)
        {
            slot = -1;
            const string prefix = "XInput#";
            if (string.IsNullOrEmpty(devicePath) || devicePath.Length == prefix.Length
                || !devicePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            int value = 0;
            for (int i = prefix.Length; i < devicePath.Length; i++)
            {
                char c = devicePath[i];
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
                if (value >= SlotCount) return false;
            }
            slot = value;
            return true;
        }

        /// <summary>The interface and generation that serve a number. A busy
        /// answer is asked again up to <paramref name="busyRetries"/> times,
        /// a millisecond apart.</summary>
        public static Answer Get(int slot, int busyRetries = 0)
        {
            for (int attempt = 0; ; attempt++)
            {
                Answer answer;
                var seam = AnswerForTest;
                if (seam != null) answer = seam(slot);
                else if (OffLimitsForTest) return new Answer(Kind.Unsupported);
                else if (slot < 0 || slot >= SlotCount) return new Answer(Kind.None);
                else answer = Ask((uint)slot);
                if (answer.Kind != Kind.Busy || attempt >= busyRetries) return answer;
                Thread.Sleep(1);
            }
        }

        /// <summary>The device an interface belongs to, as Windows names it,
        /// or null.</summary>
        public static string InstanceIdOf(string interfacePath)
        {
            var seam = InstanceIdForTest;
            if (seam != null) return seam(interfacePath);
            if (string.IsNullOrEmpty(interfacePath)) return null;
            var buf = new byte[1024];
            uint size = (uint)buf.Length;
            if (CM_Get_Device_Interface_PropertyW(interfacePath, in DEVPKEY_Device_InstanceId,
                    out uint type, buf, ref size, 0) != CR_SUCCESS
                || type != DEVPROP_TYPE_STRING || size < 4)
            {
                return null;
            }
            string id = Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
            return id.Length > 0 ? id : null;
        }

        /// <summary>Per thread: the buffer the identity call writes a path
        /// into, and the last answer found on each number. A number whose
        /// generation and path have not moved answers with the string it
        /// had, so an unchanged answer makes no garbage. OpenXInput gives a
        /// device its generation from one counter for every number when the
        /// device takes a number, and the device keeps its path while it
        /// holds it (OpenXinput.cpp, SetDeviceOnPort and
        /// DeviceInfo::Create).</summary>
        [ThreadStatic] private static char[] t_path;
        [ThreadStatic] private static Answer[] t_found;

        private static Answer Ask(uint slot)
        {
            try
            {
                var identity = new OpenXInputIdentity { cbSize = (uint)Marshal.SizeOf<OpenXInputIdentity>() };
                var buffer = t_path ??= new char[512];
                uint rc = OpenXInputGetDeviceIdentityV1(slot, ref identity, buffer, (uint)buffer.Length);
                if (rc == ERROR_INSUFFICIENT_BUFFER && identity.requiredCch > buffer.Length && identity.requiredCch <= 32768)
                {
                    buffer = t_path = new char[identity.requiredCch];
                    rc = OpenXInputGetDeviceIdentityV1(slot, ref identity, buffer, (uint)buffer.Length);
                }
                return AnswerOf(slot, rc, identity, buffer);
            }
            catch (EntryPointNotFoundException) { return new Answer(Kind.Unsupported); }
            catch (DllNotFoundException) { return new Answer(Kind.Unsupported); }
        }

        /// <summary>The answer the identity call's return code, record and
        /// path buffer make for a number. A found answer equal to the one this
        /// thread last kept for the number is that one, string and all.</summary>
        internal static Answer AnswerOf(uint slot, uint rc, in OpenXInputIdentity identity, char[] buffer)
        {
            if (rc == ERROR_SUCCESS && identity.requiredCch > 1 && identity.requiredCch <= buffer.Length)
            {
                int length = (int)identity.requiredCch - 1;
                if (slot >= SlotCount)
                    return new Answer(Kind.Found, new string(buffer, 0, length), identity.generation, identity.interfaceCount);
                ref Answer kept = ref (t_found ??= new Answer[SlotCount])[slot];
                if (kept.InterfacePath != null && kept.Generation == identity.generation
                    && kept.InterfaceCount == identity.interfaceCount
                    && kept.InterfacePath.AsSpan().SequenceEqual(buffer.AsSpan(0, length)))
                    return kept;
                return kept = new Answer(Kind.Found, new string(buffer, 0, length),
                    identity.generation, identity.interfaceCount);
            }
            return rc switch
            {
                ERROR_BUSY => new Answer(Kind.Busy),
                ERROR_NOT_SUPPORTED => new Answer(Kind.Unsupported),
                _ => new Answer(Kind.None),
            };
        }

        // ── Native ──

        /// <summary>OPENXINPUT_DEVICE_IDENTITY_V1 (OpenXinput.h): 24 bytes,
        /// generation at offset 8, as the fork's static asserts require
        /// (OpenXinput.cpp, OpenXInputGetDeviceIdentityV1).</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct OpenXInputIdentity
        {
            public uint cbSize;
            public uint requiredCch;
            public ulong generation;
            public uint interfaceIndex;
            public uint interfaceCount;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "OpenXInputGetDeviceIdentityV1", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint OpenXInputGetDeviceIdentityV1(uint userIndex, ref OpenXInputIdentity identity,
            [Out] char[] path, uint capacityCch);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_PropertyW(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceInterface, in DEVPROPKEY propertyKey,
            out uint propertyType, byte[] propertyBuffer,
            ref uint propertyBufferSize, uint ulFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        // devpkey.h:86
        private static readonly DEVPROPKEY DEVPKEY_Device_InstanceId =
            new() { fmtid = new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), pid = 256 };

        private const int CR_SUCCESS = 0;
        private const uint DEVPROP_TYPE_STRING = 0x00000012;
        private const uint ERROR_SUCCESS = 0;
        private const uint ERROR_NOT_SUPPORTED = 50;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint ERROR_BUSY = 170;
    }
}
