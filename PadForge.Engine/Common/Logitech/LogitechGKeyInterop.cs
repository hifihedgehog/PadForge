using System;
using System.Runtime.InteropServices;

namespace PadForge.Engine.Common.Logitech
{
    /// <summary>
    /// The Logitech Gaming G-key SDK's surface, as its own header declares it
    /// (issue #454, requested in discussion #449).
    ///
    /// <para>Everything here is read from <c>LogitechGkeyLib.h</c> in the SDK
    /// package. The DLL is loaded by path rather than by
    /// <see cref="DllImportAttribute"/>, because the install location is a
    /// registry lookup and a missing Logitech install must be an ordinary
    /// "not available" rather than a load failure at first call.</para>
    /// </summary>
    public static class LogitechGKeyInterop
    {
        /// <summary>Highest mouse button the SDK reports. Numbering starts at
        /// <see cref="MinMouseButton"/>, since buttons 1 through 5 are the
        /// ordinary ones Windows already delivers.</summary>
        public const int MaxMouseButton = 20;

        /// <summary>Lowest mouse button the SDK reports.</summary>
        public const int MinMouseButton = 6;

        /// <summary>Highest G-key number. Keys count from 1.</summary>
        public const int MaxGKeys = 29;

        /// <summary>Highest M-state. States count from 1, for M1, M2 and M3.</summary>
        public const int MaxMStates = 3;

        /// <summary>
        /// One G-key event, the 32-bit packed word the SDK passes by value.
        ///
        /// <para>The header declares it as C bitfields under
        /// <c>#pragma pack(push, 1)</c>: keyIdx 8 bits, keyDown 1, mState 2,
        /// mouse 1, reserved1 4, reserved2 16. That totals 32, so the word is
        /// read whole and masked here rather than described to the marshaler,
        /// which has no bitfield of its own.</para>
        ///
        /// <para><b>Logitech's own C# sample gets this wrong and must not be
        /// copied.</b> Its <c>Doc\C#Instructions.pdf</c> declares the word as
        /// a <c>ushort</c>, which is half the width the header defines, so its
        /// reserved2 shift of 16 can only ever yield zero. It also reads
        /// <c>mouse</c> as <c>(complete &gt;&gt; 11) &amp; 15</c> where the
        /// field is one bit wide, which folds three reserved bits into the
        /// answer, so its own <c>mouse == 1</c> test reads a mouse event with
        /// any of them set as a keyboard event. The header
        /// is what the DLL was compiled against, so the header wins.</para>
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct GkeyCode
        {
            /// <summary>The packed word exactly as the SDK passed it.</summary>
            public uint Raw;

            /// <summary>G-key or mouse button number. 6 means G6 or Button 6.</summary>
            public int KeyIndex => (int)(Raw & 0xFF);

            /// <summary>True while the key is held.</summary>
            public bool KeyDown => ((Raw >> 8) & 1) != 0;

            /// <summary>1, 2 or 3 for M1, M2 and M3.</summary>
            public int MState => (int)((Raw >> 9) & 3);

            /// <summary>True when the event came from a mouse. One bit, not
            /// four.</summary>
            public bool IsMouse => ((Raw >> 11) & 1) != 0;
        }

        /// <summary>
        /// The SDK's event callback.
        ///
        /// <para>The header says it "is called in the context of another
        /// thread", so an implementation may not touch anything that assumes
        /// the poll thread, and the delegate has to outlive every call native
        /// code can make through it.</para>
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogiGkeyCB(GkeyCode gkeyCode,
                                        [MarshalAs(UnmanagedType.LPWStr)] string gkeyOrButtonString,
                                        IntPtr context);

        /// <summary>The context struct handed to the callback form of init.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct LogiGkeyCBContext
        {
            public IntPtr GkeyCallBack;
            public IntPtr GkeyContext;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate bool PFN_LogiGkeyInit(IntPtr gkeyCBContext);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PFN_LogiGkeyShutdown();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate bool PFN_LogiGkeyIsMouseButtonPressed(int buttonNumber);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate bool PFN_LogiGkeyIsKeyboardGkeyPressed(int gkeyNumber, int modeNumber);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr PFN_LogiGkeyGetMouseButtonString(int buttonNumber);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr PFN_LogiGkeyGetKeyboardGkeyString(int gkeyNumber, int modeNumber);

        /// <summary>Reads a name the SDK returned. The header gives no owner
        /// for the pointer and every reference implementation copies it
        /// straight out, so this does too, and treats null as "no name".</summary>
        public static string ReadName(IntPtr p)
            => p == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUni(p) ?? string.Empty);
    }
}
