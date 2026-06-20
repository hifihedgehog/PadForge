using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Thin P/Invoke over winscard.dll (the Windows PC/SC stack, ships with
    /// Windows) for reading the UID of an NFC tag presented to a contactless
    /// smart-card reader such as the ACR122U (issue #150, Path A).
    ///
    /// Signatures and the call sequence are taken verbatim from pcsc-sharp
    /// (BSD-2-Clause): <c>src/PCSC/Interop/Windows/WinSCardAPI.cs</c> for the
    /// DllImports, and <c>Examples/MonitorReaderEvents</c> + <c>Examples/
    /// ISO7816-4/Transmit</c> for the establish / status-change / connect /
    /// transmit flow. No managed pcsc-sharp assemblies are pulled in; this is
    /// the zero-dependency hand-roll the plan recommends (option 1), matching
    /// PadForge's existing P/Invoke style (CursorControlService).
    ///
    /// The whole tag identity is its UID. There is no figure decryption, no
    /// tag writing, no keyed sectors. The "Get Data" APDU <c>FF CA 00 00 00</c>
    /// returns the UID of any ISO 14443 A/B tag (NTAG21x, MIFARE, amiibo) the
    /// reader can see, without authentication.
    /// </summary>
    internal static class WinScard
    {
        private const string DLL = "winscard.dll";

        // SCardEstablishContext scope.
        public const int SCARD_SCOPE_SYSTEM = 2;

        // Share modes / protocols for SCardConnect.
        public const int SCARD_SHARE_SHARED = 2;
        public const int SCARD_PROTOCOL_T0 = 1;
        public const int SCARD_PROTOCOL_T1 = 2;
        public const int SCARD_PROTOCOL_Tx = SCARD_PROTOCOL_T0 | SCARD_PROTOCOL_T1;

        // SCardDisconnect / End* disposition.
        public const int SCARD_LEAVE_CARD = 0;

        // SCARD_READERSTATE current/event-state bit flags.
        public const int SCARD_STATE_UNAWARE = 0x0000;
        public const int SCARD_STATE_CHANGED = 0x0002;
        public const int SCARD_STATE_UNKNOWN = 0x0004;
        public const int SCARD_STATE_EMPTY = 0x0010;
        public const int SCARD_STATE_PRESENT = 0x0020;

        // SCardGetStatusChange timeout.
        public const int INFINITE = unchecked((int)0xFFFFFFFF);

        // Return codes.
        public const int SCARD_S_SUCCESS = 0;
        public const uint SCARD_E_TIMEOUT = 0x8010000A;
        public const uint SCARD_E_CANCELLED = 0x80100002;
        public const uint SCARD_E_NO_SERVICE = 0x8010001D;
        public const uint SCARD_E_SERVICE_STOPPED = 0x8010001E;
        public const uint SCARD_E_INVALID_HANDLE = 0x80100003;
        public const uint SCARD_E_NO_READERS_AVAILABLE = 0x8010002E;
        public const uint SCARD_E_UNKNOWN_READER = 0x80100009;

        // The PnP pseudo-reader. Watching this entry in SCardGetStatusChange
        // makes the call wake when readers are added/removed, so a started
        // monitor picks up a reader plugged in after launch (pcsc-sharp
        // MonitorReaderEvents uses the same sentinel).
        public const string PNP_NOTIFICATION = "\\\\?PnP?\\Notification";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SCARD_READERSTATE
        {
            public string szReader;
            public IntPtr pvUserData;
            public int dwCurrentState;
            public int dwEventState;
            public int cbAtr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)]
            public byte[] rgbAtr;
        }

        // SCARD_IO_REQUEST header that precedes the APDU on SCardTransmit.
        // dwProtocol = the active protocol; cbPciLength = sizeof(struct) = 8.
        [StructLayout(LayoutKind.Sequential)]
        public struct SCARD_IO_REQUEST
        {
            public int dwProtocol;
            public int cbPciLength;
        }

        [DllImport(DLL, CharSet = CharSet.Unicode)]
        public static extern int SCardEstablishContext(
            int dwScope, IntPtr pvReserved1, IntPtr pvReserved2, out IntPtr phContext);

        [DllImport(DLL)]
        public static extern int SCardReleaseContext(IntPtr hContext);

        [DllImport(DLL)]
        public static extern int SCardIsValidContext(IntPtr hContext);

        [DllImport(DLL)]
        public static extern int SCardCancel(IntPtr hContext);

        [DllImport(DLL, CharSet = CharSet.Unicode, EntryPoint = "SCardListReadersW")]
        private static extern int SCardListReaders(
            IntPtr hContext, byte[] mszGroups, char[] mszReaders, ref int pcchReaders);

        [DllImport(DLL, CharSet = CharSet.Unicode, EntryPoint = "SCardConnectW")]
        public static extern int SCardConnect(
            IntPtr hContext, string szReader, int dwShareMode, int dwPreferredProtocols,
            out IntPtr phCard, out int pdwActiveProtocol);

        [DllImport(DLL)]
        public static extern int SCardDisconnect(IntPtr hCard, int dwDisposition);

        [DllImport(DLL)]
        public static extern int SCardTransmit(
            IntPtr hCard, ref SCARD_IO_REQUEST pioSendPci, byte[] pbSendBuffer, int cbSendLength,
            IntPtr pioRecvPci, byte[] pbRecvBuffer, ref int pcbRecvLength);

        [DllImport(DLL, CharSet = CharSet.Unicode, EntryPoint = "SCardGetStatusChangeW")]
        public static extern int SCardGetStatusChange(
            IntPtr hContext, int dwTimeout,
            [In, Out] SCARD_READERSTATE[] rgReaderStates, int cReaders);

        /// <summary>Enumerates reader names. Returns an empty list (never
        /// throws) when the Smart Card service is stopped or no reader is
        /// present, so an absent reader is inert exactly like absent MIDI
        /// services. Uses the size-then-fill multi-string pattern (a NUL
        /// between names, double-NUL terminator), the buffer form of
        /// SCardListReaders rather than SCARD_AUTOALLOCATE.</summary>
        public static List<string> ListReaders(IntPtr ctx)
        {
            var result = new List<string>();
            int len = 0;
            int rc = SCardListReaders(ctx, null, null, ref len);
            if (rc != SCARD_S_SUCCESS || len <= 0) return result;

            var buf = new char[len];
            rc = SCardListReaders(ctx, null, buf, ref len);
            if (rc != SCARD_S_SUCCESS) return result;

            int start = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i] == '\0')
                {
                    if (i > start)
                        result.Add(new string(buf, start, i - start));
                    start = i + 1;
                    // Double NUL terminates the multi-string.
                    if (i + 1 < buf.Length && buf[i + 1] == '\0') break;
                }
            }
            return result;
        }

        /// <summary>Connects to the card currently on <paramref name="reader"/>,
        /// sends the ISO 7816 "Get Data" APDU <c>FF CA 00 00 00</c>, and returns
        /// the tag UID as an uppercase hex string, or null on any failure (no
        /// card, foreign card without a UID, transmit error). The reader stays
        /// untouched on disconnect (SCARD_LEAVE_CARD).</summary>
        public static string ReadUid(string reader)
        {
            // Establish a short-lived context dedicated to this read. The
            // monitor thread's long-lived context must only ever be used by the
            // cancelable SCardGetStatusChange; SCardCancel cannot interrupt
            // SCardConnect/Transmit, so connecting on the monitored context
            // would let teardown release it out from under an in-flight RF
            // transmit (a native crash at shutdown). A per-read child context
            // is fully scoped to this call and independent of teardown.
            IntPtr ctx = IntPtr.Zero;
            IntPtr card = IntPtr.Zero;
            try
            {
                if (SCardEstablishContext(SCARD_SCOPE_SYSTEM, IntPtr.Zero, IntPtr.Zero, out ctx) != SCARD_S_SUCCESS
                    || ctx == IntPtr.Zero)
                    return null;

                int rc = SCardConnect(ctx, reader, SCARD_SHARE_SHARED, SCARD_PROTOCOL_Tx,
                    out card, out int activeProtocol);
                if (rc != SCARD_S_SUCCESS) return null;

                var io = new SCARD_IO_REQUEST
                {
                    // activeProtocol is T0 (1) or T1 (2); cbPciLength = 8.
                    dwProtocol = activeProtocol,
                    cbPciLength = Marshal.SizeOf<SCARD_IO_REQUEST>(),
                };

                // FF CA 00 00 00. CLA=FF, INS=CA (Get Data), P1=P2=00, Le=00
                // ("return the full UID, length unknown to the host").
                byte[] apdu = { 0xFF, 0xCA, 0x00, 0x00, 0x00 };
                byte[] recv = new byte[258]; // up to 256 data + SW1 SW2
                int recvLen = recv.Length;

                rc = SCardTransmit(card, ref io, apdu, apdu.Length, IntPtr.Zero, recv, ref recvLen);
                if (rc != SCARD_S_SUCCESS || recvLen < 2) return null;

                // Trailing status word must be 0x90 0x00 for success.
                byte sw1 = recv[recvLen - 2];
                byte sw2 = recv[recvLen - 1];
                if (sw1 != 0x90 || sw2 != 0x00) return null;

                int uidLen = recvLen - 2;
                if (uidLen <= 0) return null;

                var sb = new StringBuilder(uidLen * 2);
                for (int i = 0; i < uidLen; i++) sb.Append(recv[i].ToString("X2"));
                return sb.ToString();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (card != IntPtr.Zero)
                    SCardDisconnect(card, SCARD_LEAVE_CARD);
                if (ctx != IntPtr.Zero)
                    SCardReleaseContext(ctx);
            }
        }
    }
}
