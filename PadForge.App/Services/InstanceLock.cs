using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PadForge.Services
{
    /// <summary>
    /// The single-instance lock (#597): a mutex in a private namespace whose
    /// boundary is the Administrators group, so only a token with
    /// administrator rights can create the namespace or open it, and no
    /// program without rights can take the lock before PadForge does. The
    /// steps are Windows' own (CreateBoundaryDescriptor,
    /// AddSIDToBoundaryDescriptor, CreatePrivateNamespace,
    /// OpenPrivateNamespace), the namespace's access list grants only
    /// Administrators and the system, and the lock's name carries the
    /// Windows session, so there is one PadForge per signed-in session as
    /// there was with the old <c>Local\</c> name.
    ///
    /// <para>A copy without rights never reads the lock: it finds the
    /// running copy by its signal window, checked to run with rights, and by
    /// its pipes, checked to be PadForge's (RunningCopy, PipeOwner).</para>
    /// </summary>
    internal sealed class InstanceLock : IDisposable
    {
        /// <summary>The boundary's name and the namespace's alias.</summary>
        internal const string NamespaceName = "PadForge";

        /// <summary>The lock's name in the namespace, before the
        /// session.</summary>
        internal const string LockName = "SingleInstance";

        private IntPtr _namespace;
        private IntPtr _boundary;
        private IntPtr _mutex;

        private InstanceLock(IntPtr ns, IntPtr boundary, IntPtr mutex)
        {
            _namespace = ns;
            _boundary = boundary;
            _mutex = mutex;
        }

        /// <summary>The lock's full name in this session.</summary>
        internal static string FullName(string namespaceName, string lockName)
        {
            int session;
            using (var self = Process.GetCurrentProcess()) session = self.SessionId;
            return namespaceName + "\\" + lockName + "_" + session.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool IsTheAppsOwn(string namespaceName)
            => string.Equals(namespaceName, NamespaceName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Takes the lock. Null when the namespace cannot be made or opened,
        /// which is the case for every token without administrator rights.
        /// Otherwise the lock, owned by this thread, with
        /// <paramref name="heldElsewhere"/> set when another copy held it
        /// already. A test passes names of its own, and the test host takes
        /// nothing under the app's (RunningCopy.OffLimitsForTest).
        /// </summary>
        internal static InstanceLock Take(out bool heldElsewhere, string namespaceName = NamespaceName, string lockName = LockName)
        {
            if (RunningCopy.OffLimitsForTest && IsTheAppsOwn(namespaceName)) { heldElsewhere = false; return null; }
            heldElsewhere = false;
            if (!OpenNamespace(namespaceName, create: true, out IntPtr ns, out IntPtr boundary)) return null;
            IntPtr mutex = CreateMutexW(IntPtr.Zero, true, FullName(namespaceName, lockName));
            int error = Marshal.GetLastWin32Error();
            if (mutex == IntPtr.Zero)
            {
                Close(ns, boundary);
                return null;
            }
            heldElsewhere = error == ERROR_ALREADY_EXISTS;
            return new InstanceLock(ns, boundary, mutex);
        }

        /// <summary>Whether another copy holds the lock now. False for a
        /// caller without rights, which cannot open the namespace.</summary>
        internal static bool IsHeld(string namespaceName = NamespaceName, string lockName = LockName)
        {
            if (RunningCopy.OffLimitsForTest && IsTheAppsOwn(namespaceName)) return false;
            if (!OpenNamespace(namespaceName, create: false, out IntPtr ns, out IntPtr boundary)) return false;
            try
            {
                IntPtr mutex = OpenMutexW(SYNCHRONIZE, false, FullName(namespaceName, lockName));
                if (mutex == IntPtr.Zero) return false;
                CloseHandle(mutex);
                return true;
            }
            finally { Close(ns, boundary); }
        }

        /// <summary>The namespace with the Administrators boundary: made
        /// when <paramref name="create"/> asks and none stands, opened
        /// otherwise. False when neither works.</summary>
        private static bool OpenNamespace(string name, bool create, out IntPtr ns, out IntPtr boundary)
        {
            ns = IntPtr.Zero;
            boundary = CreateBoundaryDescriptorW(name, 0);
            if (boundary == IntPtr.Zero) return false;
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var sid = new byte[administrators.BinaryLength];
            administrators.GetBinaryForm(sid, 0);
            if (!AddSIDToBoundaryDescriptor(ref boundary, sid))
            {
                DeleteBoundaryDescriptor(boundary);
                boundary = IntPtr.Zero;
                return false;
            }
            if (create)
            {
                ns = CreateNamespace(boundary, name);
                if (ns == IntPtr.Zero && Marshal.GetLastWin32Error() != ERROR_ALREADY_EXISTS)
                {
                    DeleteBoundaryDescriptor(boundary);
                    boundary = IntPtr.Zero;
                    return false;
                }
            }
            if (ns == IntPtr.Zero) ns = OpenPrivateNamespaceW(boundary, name);
            if (ns != IntPtr.Zero) return true;
            DeleteBoundaryDescriptor(boundary);
            boundary = IntPtr.Zero;
            return false;
        }

        /// <summary>Makes the namespace with an access list that grants
        /// Administrators and the system alone.</summary>
        private static IntPtr CreateNamespace(IntPtr boundary, string name)
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;GA;;;BA)(A;;GA;;;SY)", 1, out IntPtr descriptor, IntPtr.Zero))
                return IntPtr.Zero;
            try
            {
                var attributes = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = descriptor,
                    bInheritHandle = false,
                };
                IntPtr ns = CreatePrivateNamespaceW(ref attributes, boundary, name);
                int error = Marshal.GetLastWin32Error();
                Marshal.SetLastPInvokeError(error);
                return ns;
            }
            finally { LocalFree(descriptor); }
        }

        private static void Close(IntPtr ns, IntPtr boundary)
        {
            if (ns != IntPtr.Zero) ClosePrivateNamespace(ns, 0);
            if (boundary != IntPtr.Zero) DeleteBoundaryDescriptor(boundary);
        }

        public void Dispose()
        {
            if (_mutex != IntPtr.Zero)
            {
                ReleaseMutex(_mutex);
                CloseHandle(_mutex);
                _mutex = IntPtr.Zero;
            }
            Close(_namespace, _boundary);
            _namespace = IntPtr.Zero;
            _boundary = IntPtr.Zero;
        }

        private const int ERROR_ALREADY_EXISTS = 183;
        private const uint SYNCHRONIZE = 0x00100000;

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateBoundaryDescriptorW(string name, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddSIDToBoundaryDescriptor(ref IntPtr boundaryDescriptor, byte[] requiredSid);

        [DllImport("kernel32.dll")]
        private static extern void DeleteBoundaryDescriptor(IntPtr boundaryDescriptor);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreatePrivateNamespaceW(ref SECURITY_ATTRIBUTES attributes, IntPtr boundaryDescriptor, string aliasPrefix);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenPrivateNamespaceW(IntPtr boundaryDescriptor, string aliasPrefix);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClosePrivateNamespace(IntPtr handle, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateMutexW(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenMutexW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReleaseMutex(IntPtr mutex);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
            out IntPtr descriptor, IntPtr size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
