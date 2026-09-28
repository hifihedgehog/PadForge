using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// The named mutexes the Soup-lineage readers take around each exchange
    /// with a polled keyboard (issue #468): Soup's getActiveKeysDrunkdeer,
    /// getActiveKeysKeychron and getActiveKeysMadlions
    /// (AnalogueKeyboard.cpp:765, 902, 1128) and HallJoy's plugin build of them
    /// (UniversalAnalogPluginFixed overlay AnalogueKeyboard.cpp:805, 982,
    /// 1238). Two programs polling one keyboard receive each other's answers,
    /// and the lock keeps their requests from interleaving.
    ///
    /// <para>The names carry no prefix, as Soup's CreateMutexA calls do, so
    /// they land in the session's namespace beside Soup's. PadForge runs
    /// elevated, and a mutex it created with the default security would deny
    /// a reader that is not: Soup asserts when CreateMutexA fails
    /// (NamedMutex.hpp:13-17). So PadForge's mutex grants every user full
    /// access and carries the low integrity label.</para>
    /// </summary>
    public sealed class AnalogKeyboardNamedMutex
    {
        public const string DrunkDeer = "DrunkDeerMtx";
        public const string Keychron = "KeychronMtx";
        public const string Madlions = "MadlionsMtx";

        /// <summary>How long a pass waits for another reader's exchange:
        /// HallJoy's wait before its DrunkDeer identity request
        /// (UniversalAnalogPluginFixed main.cpp:418-421).</summary>
        public const int WaitMs = 100;

        /// <summary>Everyone full access, low integrity with no write up.</summary>
        private const string Sddl = "D:(A;;GA;;;WD)S:(ML;;NW;;;LW)";

        private static readonly object s_lock = new();
        private static readonly Dictionary<string, AnalogKeyboardNamedMutex> s_byName = new(StringComparer.Ordinal);

        private readonly IntPtr _handle;

        private AnalogKeyboardNamedMutex(IntPtr handle)
        {
            _handle = handle;
        }

        /// <summary>The process's handle to the mutex, created or opened on
        /// first use and kept for the life of the process, as HallJoy keeps its
        /// static MadlionsMtx. Null when it can be neither created nor opened,
        /// and then the reader goes on without it.</summary>
        public static AnalogKeyboardNamedMutex Get(string name)
        {
            lock (s_lock)
            {
                if (s_byName.TryGetValue(name, out var known)) return known;
                var created = Create(name);
                s_byName[name] = created;
                return created;
            }
        }

        /// <summary>Takes the mutex, waiting up to <paramref name="timeoutMs"/>.
        /// An abandoned mutex counts as taken, as Soup's tryLock counts it.
        /// Release it on the same thread.</summary>
        public bool Wait(int timeoutMs = WaitMs)
        {
            uint result = WaitForSingleObject(_handle, (uint)Math.Max(0, timeoutMs));
            return result == WaitObject0 || result == WaitAbandoned;
        }

        public void Release() => ReleaseMutex(_handle);

        private static AnalogKeyboardNamedMutex Create(string name)
        {
            IntPtr descriptor = IntPtr.Zero;
            try
            {
                IntPtr handle = IntPtr.Zero;
                if (ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, SddlRevision1, out descriptor, IntPtr.Zero))
                {
                    var attributes = new SecurityAttributes
                    {
                        Length = Marshal.SizeOf<SecurityAttributes>(),
                        SecurityDescriptor = descriptor,
                        InheritHandle = 0,
                    };
                    handle = CreateMutexW(ref attributes, false, name);
                }
                // A mutex another program created with narrower rights can
                // still be opened for waiting and releasing.
                if (handle == IntPtr.Zero)
                    handle = OpenMutexW(Synchronize | MutexModifyState, false, name);
                return handle == IntPtr.Zero ? null : new AnalogKeyboardNamedMutex(handle);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            }
        }

        private const uint SddlRevision1 = 1;
        private const uint Synchronize = 0x00100000;
        private const uint MutexModifyState = 0x0001;
        private const uint WaitObject0 = 0x00000000;
        private const uint WaitAbandoned = 0x00000080;

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            public int InheritHandle;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string stringSecurityDescriptor, uint revision, out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateMutexW(ref SecurityAttributes attributes, bool initialOwner, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenMutexW(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReleaseMutex(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
