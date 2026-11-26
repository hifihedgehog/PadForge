using System;
using System.Runtime.InteropServices;

namespace PadForge.Common
{
    /// <summary>
    /// Windows calls made through the System32 copies of kernel32.dll and
    /// advapi32.dll, loaded by full path, before a start with rights may trust
    /// the folder its native libraries were unpacked into (#600).
    ///
    /// <para>The runtime looks for every library a <c>DllImport</c> names in
    /// the folders the host lists before it asks Windows (nativelibrary.cpp,
    /// LoadNativeLibraryBySearch and LoadFromNativeDllSearchDirectories), and
    /// the single-file host lists the folder it unpacks into. That folder is
    /// in the user's temp folder unless DOTNET_BUNDLE_EXTRACT_BASE_DIR names
    /// another, and the host reuses what it finds there (extractor.cpp,
    /// verify_recover_extraction checks only that each file exists). Measured
    /// on this PC: a kernel32.dll or advapi32.dll put there is loaded by the
    /// first <c>DllImport</c> of that name, the runtime's own included. So
    /// until <see cref="PadForge.Services.ProtectedStart"/> has decided, the
    /// process makes no <c>DllImport</c> call. Each call here goes through a
    /// function pointer taken from a module loaded by its full path, which
    /// the runtime loads as given (NativeLibrary.Load), and System32's path
    /// comes from the page Windows maps into every process at 0x7FFE0000
    /// (KUSER_SHARED_DATA.NtSystemRoot, offset 0x30), which no environment
    /// variable can change.</para>
    ///
    /// <para>No member throws, since building an exception's message reads
    /// the user's language through a <c>DllImport</c>. A call whose module
    /// or export is missing answers as a failed Windows call does.</para>
    /// </summary>
    internal static unsafe class System32Calls
    {
        private const int SystemRootOffset = 0x30;
        private const int SystemRootLength = 260;
        private const long SharedUserData = 0x7FFE0000;

        /// <summary>Windows' folder, such as <c>C:\WINDOWS</c>, from the page
        /// the kernel maps into every process. Empty when it cannot be
        /// read.</summary>
        internal static readonly string WindowsFolder = ReadSystemRoot();

        /// <summary>System32 with its trailing separator.</summary>
        internal static readonly string System32 = WindowsFolder.Length == 0 ? string.Empty : WindowsFolder + @"\System32\";

        private static readonly IntPtr Kernel32 = Load("kernel32.dll");
        private static readonly IntPtr Advapi32 = Load("advapi32.dll");

        private static string ReadSystemRoot()
        {
            char* root = (char*)(SharedUserData + SystemRootOffset);
            int length = 0;
            while (length < SystemRootLength && root[length] != '\0') length++;
            // A drive, a colon and a separator at least.
            if (length < 3 || root[1] != ':' || root[2] != '\\') return string.Empty;
            return new string(root, 0, length).TrimEnd('\\');
        }

        private static IntPtr Load(string name)
            => System32.Length != 0 && NativeLibrary.TryLoad(System32 + name, out IntPtr module) ? module : IntPtr.Zero;

        private static IntPtr Find(IntPtr module, string export)
            => module != IntPtr.Zero && NativeLibrary.TryGetExport(module, export, out IntPtr address) ? address : IntPtr.Zero;

        // Each pointer is taken once. Zero when the export is missing.
        private static readonly IntPtr pGetEnvironmentVariableW = Find(Kernel32, "GetEnvironmentVariableW");
        private static readonly IntPtr pSetEnvironmentVariableW = Find(Kernel32, "SetEnvironmentVariableW");
        private static readonly IntPtr pSetDefaultDllDirectories = Find(Kernel32, "SetDefaultDllDirectories");
        private static readonly IntPtr pGetModuleFileNameW = Find(Kernel32, "GetModuleFileNameW");
        private static readonly IntPtr pGetCommandLineW = Find(Kernel32, "GetCommandLineW");
        private static readonly IntPtr pGetEnvironmentStringsW = Find(Kernel32, "GetEnvironmentStringsW");
        private static readonly IntPtr pFreeEnvironmentStringsW = Find(Kernel32, "FreeEnvironmentStringsW");
        private static readonly IntPtr pGetStartupInfoW = Find(Kernel32, "GetStartupInfoW");
        private static readonly IntPtr pCreateProcessW = Find(Kernel32, "CreateProcessW");
        private static readonly IntPtr pWaitForMultipleObjects = Find(Kernel32, "WaitForMultipleObjects");
        private static readonly IntPtr pGetExitCodeProcess = Find(Kernel32, "GetExitCodeProcess");
        private static readonly IntPtr pCloseHandle = Find(Kernel32, "CloseHandle");
        private static readonly IntPtr pCreateEventW = Find(Kernel32, "CreateEventW");
        private static readonly IntPtr pSetEvent = Find(Kernel32, "SetEvent");
        private static readonly IntPtr pAttachConsole = Find(Kernel32, "AttachConsole");
        private static readonly IntPtr pCreateDirectoryW = Find(Kernel32, "CreateDirectoryW");
        private static readonly IntPtr pGetFileAttributesW = Find(Kernel32, "GetFileAttributesW");
        private static readonly IntPtr pMoveFileExW = Find(Kernel32, "MoveFileExW");
        private static readonly IntPtr pCreateFileW = Find(Kernel32, "CreateFileW");
        private static readonly IntPtr pGetFileInformationByHandle = Find(Kernel32, "GetFileInformationByHandle");
        private static readonly IntPtr pGetFullPathNameW = Find(Kernel32, "GetFullPathNameW");
        private static readonly IntPtr pCompareStringOrdinal = Find(Kernel32, "CompareStringOrdinal");
        private static readonly IntPtr pGetCurrentProcessId = Find(Kernel32, "GetCurrentProcessId");
        private static readonly IntPtr pGetTickCount64 = Find(Kernel32, "GetTickCount64");
        private static readonly IntPtr pLocalFree = Find(Kernel32, "LocalFree");
        private static readonly IntPtr pExitProcess = Find(Kernel32, "ExitProcess");
        private static readonly IntPtr pCreateJobObjectW = Find(Kernel32, "CreateJobObjectW");
        private static readonly IntPtr pSetInformationJobObject = Find(Kernel32, "SetInformationJobObject");
        private static readonly IntPtr pAssignProcessToJobObject = Find(Kernel32, "AssignProcessToJobObject");
        private static readonly IntPtr pResumeThread = Find(Kernel32, "ResumeThread");
        private static readonly IntPtr pTerminateProcess = Find(Kernel32, "TerminateProcess");
        private static readonly IntPtr pOpenProcessToken = Find(Advapi32, "OpenProcessToken");
        private static readonly IntPtr pGetTokenInformation = Find(Advapi32, "GetTokenInformation");
        private static readonly IntPtr pGetSecurityInfo = Find(Advapi32, "GetSecurityInfo");
        private static readonly IntPtr pGetSecurityDescriptorLength = Find(Advapi32, "GetSecurityDescriptorLength");
        private static readonly IntPtr pConvertStringSdToSd = Find(Advapi32, "ConvertStringSecurityDescriptorToSecurityDescriptorW");
        private static readonly IntPtr pRegGetValueW = Find(Advapi32, "RegGetValueW");
        private static readonly IntPtr pGetLastError = Find(Kernel32, "GetLastError");

        internal static readonly IntPtr InvalidHandle = new IntPtr(-1);
        internal static readonly IntPtr CurrentProcess = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            public int Inherit;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInformation
        {
            public uint Attributes;
            public uint CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh, LastWriteTimeLow, LastWriteTimeHigh;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        /// <summary>Whether every call this class makes could be found. A
        /// process where they could not cannot check its folders and does
        /// not trust them.</summary>
        internal static bool Ready =>
            pGetEnvironmentVariableW != IntPtr.Zero && pSetEnvironmentVariableW != IntPtr.Zero
            && pSetDefaultDllDirectories != IntPtr.Zero && pGetModuleFileNameW != IntPtr.Zero
            && pGetCommandLineW != IntPtr.Zero && pGetEnvironmentStringsW != IntPtr.Zero
            && pFreeEnvironmentStringsW != IntPtr.Zero && pGetStartupInfoW != IntPtr.Zero
            && pCreateProcessW != IntPtr.Zero && pWaitForMultipleObjects != IntPtr.Zero
            && pGetExitCodeProcess != IntPtr.Zero && pCloseHandle != IntPtr.Zero
            && pCreateEventW != IntPtr.Zero && pSetEvent != IntPtr.Zero
            && pAttachConsole != IntPtr.Zero && pCreateDirectoryW != IntPtr.Zero
            && pGetFileAttributesW != IntPtr.Zero && pMoveFileExW != IntPtr.Zero && pCreateFileW != IntPtr.Zero
            && pGetFileInformationByHandle != IntPtr.Zero && pGetFullPathNameW != IntPtr.Zero
            && pCompareStringOrdinal != IntPtr.Zero && pGetCurrentProcessId != IntPtr.Zero
            && pGetTickCount64 != IntPtr.Zero && pLocalFree != IntPtr.Zero && pExitProcess != IntPtr.Zero
            && pCreateJobObjectW != IntPtr.Zero && pSetInformationJobObject != IntPtr.Zero
            && pAssignProcessToJobObject != IntPtr.Zero && pResumeThread != IntPtr.Zero && pTerminateProcess != IntPtr.Zero
            && pOpenProcessToken != IntPtr.Zero
            && pGetTokenInformation != IntPtr.Zero && pGetSecurityInfo != IntPtr.Zero
            && pGetSecurityDescriptorLength != IntPtr.Zero && pConvertStringSdToSd != IntPtr.Zero
            && pRegGetValueW != IntPtr.Zero;

        // ── Process and environment ──

        /// <summary>The variable's value, null when it is not set.</summary>
        internal static string GetVariable(string name)
        {
            if (pGetEnvironmentVariableW == IntPtr.Zero) return null;
            var call = (delegate* unmanaged<char*, char*, uint, uint>)pGetEnvironmentVariableW;
            fixed (char* n = name)
            {
                uint needed = call(n, null, 0);
                if (needed == 0) return null;
                char[] buffer = new char[needed];
                fixed (char* b = buffer)
                {
                    // A value that grew between the two calls is not read.
                    uint got = call(n, b, needed);
                    return got >= needed ? null : new string(b, 0, (int)got);
                }
            }
        }

        /// <summary>Sets a variable of this process, or removes it with
        /// null.</summary>
        internal static bool SetVariable(string name, string value)
        {
            if (pSetEnvironmentVariableW == IntPtr.Zero) return false;
            var call = (delegate* unmanaged<char*, char*, int>)pSetEnvironmentVariableW;
            fixed (char* n = name)
            fixed (char* v = value)
                return call(n, value == null ? null : v) != 0;
        }

        /// <summary>LOAD_LIBRARY_SEARCH_DEFAULT_DIRS: a library named without a
        /// folder is looked for in the exe's folder, System32 and folders
        /// added with AddDllDirectory, never in the working folder or
        /// PATH.</summary>
        internal const uint SearchDefaultDirs = 0x00001000;

        internal static bool SetDefaultDllDirectories(uint flags)
            => pSetDefaultDllDirectories != IntPtr.Zero && ((delegate* unmanaged<uint, int>)pSetDefaultDllDirectories)(flags) != 0;

        /// <summary>This exe's full path, or null.</summary>
        internal static string OwnExe()
        {
            if (pGetModuleFileNameW == IntPtr.Zero) return null;
            var call = (delegate* unmanaged<IntPtr, char*, uint, uint>)pGetModuleFileNameW;
            for (uint size = 260; size <= 32768; size *= 2)
            {
                char[] buffer = new char[size];
                fixed (char* b = buffer)
                {
                    uint got = call(IntPtr.Zero, b, size);
                    if (got == 0) return null;
                    if (got < size) return new string(b, 0, (int)got);
                }
            }
            return null;
        }

        /// <summary>The command line as this process was given it.</summary>
        internal static string CommandLine()
            => pGetCommandLineW == IntPtr.Zero ? null : new string(((delegate* unmanaged<char*>)pGetCommandLineW)());

        /// <summary>This process's variables as <c>name=value</c> lines, in
        /// the order Windows keeps them. Null when they cannot be read.</summary>
        internal static string[] Variables()
        {
            if (pGetEnvironmentStringsW == IntPtr.Zero || pFreeEnvironmentStringsW == IntPtr.Zero) return null;
            char* block = ((delegate* unmanaged<char*>)pGetEnvironmentStringsW)();
            if (block == null) return null;
            try
            {
                return Lines(block);
            }
            finally
            {
                ((delegate* unmanaged<char*, int>)pFreeEnvironmentStringsW)(block);
            }
        }

        /// <summary>userenv.dll by System32's full path, loaded at the first
        /// read of the machine's variables.</summary>
        private static IntPtr s_userenv;

        /// <summary>
        /// The machine's own variables as <c>name=value</c> lines: the block
        /// CreateEnvironmentBlock builds with no token, which holds the
        /// system's variables only (Microsoft, CreateEnvironmentBlock), read
        /// from the machine's part of the registry, so no value a user set
        /// replaces one of them. Null when Windows refused. A process with
        /// rights builds the variables of every process it starts from these
        /// (<see cref="PadForge.Services.RightsVariables"/>).
        /// </summary>
        internal static string[] MachineVariables() => VariablesOf(IntPtr.Zero);

        /// <summary>
        /// The variables Windows builds for the user of
        /// <paramref name="token"/> from that user's profile, with the
        /// machine's under them (CreateEnvironmentBlock, not inheriting this
        /// process's), as <c>name=value</c> lines, or the machine's alone
        /// with no token. Null when Windows refused.
        /// </summary>
        internal static string[] VariablesOf(IntPtr token) => VariablesOf(token, out _);

        /// <summary>The same, with the Windows error of a refusal in
        /// <paramref name="error"/>, 0 when the variables were read. A
        /// primary token needs TOKEN_QUERY and TOKEN_DUPLICATE (Microsoft,
        /// CreateEnvironmentBlock).</summary>
        internal static string[] VariablesOf(IntPtr token, out int error)
        {
            error = 0;
            if (s_userenv == IntPtr.Zero) s_userenv = Load("userenv.dll");
            IntPtr create = Find(s_userenv, "CreateEnvironmentBlock");
            IntPtr destroy = Find(s_userenv, "DestroyEnvironmentBlock");
            if (create == IntPtr.Zero || destroy == IntPtr.Zero)
            {
                error = ErrorProcNotFound;
                return null;
            }
            void* block = null;
            if (((delegate* unmanaged<void**, IntPtr, int, int>)create)(&block, token, 0) == 0 || block == null)
            {
                error = LastError();
                return null;
            }
            try
            {
                return Lines((char*)block);
            }
            finally
            {
                ((delegate* unmanaged<void*, int>)destroy)(block);
            }
        }

        /// <summary>
        /// The variables Windows builds for the user <paramref name="process"/>
        /// runs as, from that user's profile and the machine's
        /// (<see cref="VariablesOf(IntPtr, out int)"/> on the process's own
        /// token, opened with TOKEN_QUERY and TOKEN_DUPLICATE), never this
        /// process's. The handle needs PROCESS_QUERY_LIMITED_INFORMATION
        /// (Microsoft, OpenProcessToken). Null with the Windows error in
        /// <paramref name="error"/> when the token or its variables cannot be
        /// read.
        /// </summary>
        internal static string[] VariablesOfProcess(IntPtr process, out int error)
        {
            error = 0;
            if (pOpenProcessToken == IntPtr.Zero)
            {
                error = ErrorProcNotFound;
                return null;
            }
            IntPtr token = IntPtr.Zero;
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)pOpenProcessToken)(process, TokenQuery | TokenDuplicateAccess, &token) == 0)
            {
                error = LastError();
                if (error == 0) error = ErrorAccessDenied;
                return null;
            }
            try
            {
                string[] lines = VariablesOf(token, out error);
                if (lines == null && error == 0) error = ErrorAccessDenied;
                return lines;
            }
            finally
            {
                Close(token);
            }
        }

        private const int ErrorAccessDenied = 5;
        private const int ErrorProcNotFound = 127;
        private const uint TokenDuplicateAccess = 0x0002;

        /// <summary>The calling thread's last Windows error, read through
        /// System32's kernel32 at once after the call that set it.</summary>
        private static int LastError()
            => pGetLastError == IntPtr.Zero ? 0 : unchecked((int)((delegate* unmanaged<uint>)pGetLastError)());

        /// <summary>The lines of a block Windows keeps: each ends in a null
        /// character, and the block in one more.</summary>
        private static string[] Lines(char* block)
        {
            int count = 0;
            for (char* p = block; *p != '\0'; p += Length(p) + 1) count++;
            var lines = new string[count];
            int i = 0;
            for (char* p = block; *p != '\0'; p += Length(p) + 1) lines[i++] = new string(p);
            return lines;
        }

        private static int Length(char* text)
        {
            int n = 0;
            while (text[n] != '\0') n++;
            return n;
        }

        internal static void GetStartupInfo(out StartupInfo info)
        {
            info = default;
            info.cb = sizeof(StartupInfo);
            if (pGetStartupInfoW == IntPtr.Zero) return;
            fixed (StartupInfo* p = &info) ((delegate* unmanaged<StartupInfo*, void>)pGetStartupInfoW)(p);
        }

        internal const uint CreateUnicodeEnvironment = 0x00000400;
        internal const uint CreateSuspended = 0x00000004;

        /// <summary>Starts <paramref name="exe"/> suspended, with the command
        /// line and variables given, in <paramref name="folder"/>, handing on
        /// every handle this process may hand on. False when Windows
        /// refused.</summary>
        internal static bool CreateProcess(string exe, string commandLine, string environmentBlock, string folder,
            ref StartupInfo startup, out ProcessInformation process)
        {
            process = default;
            if (pCreateProcessW == IntPtr.Zero) return false;
            var call = (delegate* unmanaged<char*, char*, void*, void*, int, uint, void*, char*, StartupInfo*, ProcessInformation*, int>)pCreateProcessW;
            // CreateProcessW may write to the command line, so it gets a copy
            // of its own (Microsoft, CreateProcessW).
            char[] line = new char[commandLine.Length + 1];
            commandLine.CopyTo(0, line, 0, commandLine.Length);
            fixed (char* e = exe)
            fixed (char* l = line)
            fixed (char* env = environmentBlock)
            fixed (char* f = folder)
            fixed (StartupInfo* s = &startup)
            fixed (ProcessInformation* pi = &process)
                return call(e, l, null, null, 1, CreateUnicodeEnvironment | CreateSuspended, env, f, s, pi) != 0;
        }

        internal static bool Resume(IntPtr thread)
            => pResumeThread != IntPtr.Zero && ((delegate* unmanaged<IntPtr, uint>)pResumeThread)(thread) != uint.MaxValue;

        internal static void Terminate(IntPtr process, uint code)
        {
            if (pTerminateProcess != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint, int>)pTerminateProcess)(process, code);
        }

        // ── Jobs: the layout DsxStandIn's job object uses, Microsoft's
        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION ──

        [StructLayout(LayoutKind.Sequential)]
        private struct JobLimits
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        /// <summary>A job whose processes end when its last handle closes, or
        /// zero.</summary>
        internal static IntPtr NewKillOnCloseJob()
        {
            if (pCreateJobObjectW == IntPtr.Zero) return IntPtr.Zero;
            IntPtr job = ((delegate* unmanaged<void*, char*, IntPtr>)pCreateJobObjectW)(null, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            if (SetKillOnClose(job, true)) return job;
            Close(job);
            return IntPtr.Zero;
        }

        /// <summary>Turns a job's end-with-its-last-handle on or off.</summary>
        internal static bool SetKillOnClose(IntPtr job, bool on)
        {
            if (pSetInformationJobObject == IntPtr.Zero) return false;
            var limits = new JobLimits { LimitFlags = on ? JobObjectLimitKillOnJobClose : 0 };
            return ((delegate* unmanaged<IntPtr, int, JobLimits*, uint, int>)pSetInformationJobObject)(
                job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JobLimits)) != 0;
        }

        internal static bool AssignToJob(IntPtr job, IntPtr process)
            => pAssignProcessToJobObject != IntPtr.Zero
               && ((delegate* unmanaged<IntPtr, IntPtr, int>)pAssignProcessToJobObject)(job, process) != 0;

        internal const uint WaitObject0 = 0;
        internal const uint Infinite = 0xFFFFFFFF;

        /// <summary>Waits for the first of two handles. Its index, or a value
        /// past 1 when the wait failed.</summary>
        internal static uint WaitForFirst(IntPtr first, IntPtr second)
        {
            if (pWaitForMultipleObjects == IntPtr.Zero) return uint.MaxValue;
            IntPtr* handles = stackalloc IntPtr[2];
            handles[0] = first;
            handles[1] = second;
            return ((delegate* unmanaged<uint, IntPtr*, int, uint, uint>)pWaitForMultipleObjects)(2, handles, 0, Infinite);
        }

        internal static bool GetExitCode(IntPtr process, out uint code)
        {
            code = 0;
            if (pGetExitCodeProcess == IntPtr.Zero) return false;
            fixed (uint* c = &code) return ((delegate* unmanaged<IntPtr, uint*, int>)pGetExitCodeProcess)(process, c) != 0;
        }

        internal static void Close(IntPtr handle)
        {
            if (handle != IntPtr.Zero && handle != InvalidHandle && pCloseHandle != IntPtr.Zero)
                ((delegate* unmanaged<IntPtr, int>)pCloseHandle)(handle);
        }

        internal static bool SetEvent(IntPtr handle)
            => pSetEvent != IntPtr.Zero && ((delegate* unmanaged<IntPtr, int>)pSetEvent)(handle) != 0;

        // ── The Administrators namespace of the handover (#600) ──

        private static readonly IntPtr pCreateBoundaryDescriptorW = Find(Kernel32, "CreateBoundaryDescriptorW");
        private static readonly IntPtr pAddSIDToBoundaryDescriptor = Find(Kernel32, "AddSIDToBoundaryDescriptor");
        private static readonly IntPtr pDeleteBoundaryDescriptor = Find(Kernel32, "DeleteBoundaryDescriptor");
        private static readonly IntPtr pCreatePrivateNamespaceW = Find(Kernel32, "CreatePrivateNamespaceW");
        private static readonly IntPtr pOpenPrivateNamespaceW = Find(Kernel32, "OpenPrivateNamespaceW");
        private static readonly IntPtr pClosePrivateNamespace = Find(Kernel32, "ClosePrivateNamespace");
        private static readonly IntPtr pOpenEventW = Find(Kernel32, "OpenEventW");

        // S-1-5-32-544, the Administrators group, in its binary form.
        private static readonly byte[] AdministratorsSid = { 1, 2, 0, 0, 0, 0, 0, 5, 32, 0, 0, 0, 32, 2, 0, 0 };
        private const int ErrorAlreadyExists = 183;
        private const uint EventModifyState = 0x0002;

        /// <summary>
        /// The private namespace of that name whose boundary is the
        /// Administrators group, as InstanceLock makes it: made with an access
        /// list that grants Administrators and the system alone when
        /// <paramref name="create"/> asks and none stands, opened otherwise.
        /// Only a token with administrator rights can make or open it. Zero
        /// when neither works. <paramref name="boundary"/> is closed with it
        /// (<see cref="CloseNamespace"/>).
        /// </summary>
        internal static IntPtr OpenAdministratorsNamespace(string name, bool create, out IntPtr boundary)
        {
            boundary = IntPtr.Zero;
            if (pCreateBoundaryDescriptorW == IntPtr.Zero || pAddSIDToBoundaryDescriptor == IntPtr.Zero || pDeleteBoundaryDescriptor == IntPtr.Zero
                || pCreatePrivateNamespaceW == IntPtr.Zero || pOpenPrivateNamespaceW == IntPtr.Zero || pConvertStringSdToSd == IntPtr.Zero
                || pLocalFree == IntPtr.Zero || string.IsNullOrEmpty(name))
                return IntPtr.Zero;
            IntPtr made;
            fixed (char* n = name) made = ((delegate* unmanaged<char*, uint, IntPtr>)pCreateBoundaryDescriptorW)(n, 0);
            if (made == IntPtr.Zero) return IntPtr.Zero;
            int added;
            fixed (byte* sid = AdministratorsSid) added = ((delegate* unmanaged<IntPtr*, byte*, int>)pAddSIDToBoundaryDescriptor)(&made, sid);
            if (added == 0)
            {
                ((delegate* unmanaged<IntPtr, void>)pDeleteBoundaryDescriptor)(made);
                return IntPtr.Zero;
            }
            IntPtr ns = IntPtr.Zero;
            if (create)
            {
                IntPtr descriptor = IntPtr.Zero;
                const uint SddlRevision1 = 1;
                fixed (char* s = "D:(A;;GA;;;BA)(A;;GA;;;SY)")
                    if (((delegate* unmanaged<char*, uint, IntPtr*, uint*, int>)pConvertStringSdToSd)(s, SddlRevision1, &descriptor, null) == 0)
                        descriptor = IntPtr.Zero;
                if (descriptor != IntPtr.Zero)
                {
                    try
                    {
                        var sa = new SecurityAttributes { Length = sizeof(SecurityAttributes), Descriptor = descriptor, Inherit = 0 };
                        fixed (char* n = name) ns = ((delegate* unmanaged<SecurityAttributes*, IntPtr, char*, IntPtr>)pCreatePrivateNamespaceW)(&sa, made, n);
                    }
                    finally
                    {
                        ((delegate* unmanaged<IntPtr, IntPtr>)pLocalFree)(descriptor);
                    }
                }
            }
            if (ns == IntPtr.Zero)
                fixed (char* n = name) ns = ((delegate* unmanaged<IntPtr, char*, IntPtr>)pOpenPrivateNamespaceW)(made, n);
            if (ns == IntPtr.Zero)
            {
                ((delegate* unmanaged<IntPtr, void>)pDeleteBoundaryDescriptor)(made);
                return IntPtr.Zero;
            }
            boundary = made;
            return ns;
        }

        /// <summary>Closes a namespace <see cref="OpenAdministratorsNamespace"/>
        /// gave, and its boundary.</summary>
        internal static void CloseNamespace(IntPtr ns, IntPtr boundary)
        {
            if (ns != IntPtr.Zero && pClosePrivateNamespace != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint, int>)pClosePrivateNamespace)(ns, 0);
            if (boundary != IntPtr.Zero && pDeleteBoundaryDescriptor != IntPtr.Zero) ((delegate* unmanaged<IntPtr, void>)pDeleteBoundaryDescriptor)(boundary);
        }

        /// <summary>A new event of that full name, set by hand, handed on to
        /// no child. Zero when it cannot be made or the name is taken.</summary>
        internal static IntPtr NewNamedEvent(string fullName)
        {
            if (pCreateEventW == IntPtr.Zero || string.IsNullOrEmpty(fullName)) return IntPtr.Zero;
            var sa = new SecurityAttributes { Length = sizeof(SecurityAttributes), Inherit = 0 };
            IntPtr handle;
            fixed (char* n = fullName) handle = ((delegate* unmanaged<SecurityAttributes*, int, int, char*, IntPtr>)pCreateEventW)(&sa, 1, 0, n);
            if (handle != IntPtr.Zero && LastError() == ErrorAlreadyExists)
            {
                Close(handle);
                return IntPtr.Zero;
            }
            return handle;
        }

        /// <summary>The event of that full name, opened to be set. Zero when
        /// there is none or it may not be opened.</summary>
        internal static IntPtr OpenNamedEvent(string fullName)
        {
            if (pOpenEventW == IntPtr.Zero || string.IsNullOrEmpty(fullName)) return IntPtr.Zero;
            fixed (char* n = fullName) return ((delegate* unmanaged<uint, int, char*, IntPtr>)pOpenEventW)(EventModifyState, 0, n);
        }

        private const uint AttachParentProcess = 0xFFFFFFFF;

        /// <summary>Attaches to the console of the process that started this
        /// one, if it has one, so a child that attaches to its own parent's
        /// console reaches the same.</summary>
        internal static bool AttachToParentConsole()
            => pAttachConsole != IntPtr.Zero && ((delegate* unmanaged<uint, int>)pAttachConsole)(AttachParentProcess) != 0;

        /// <summary>Ends this process at once with <paramref name="code"/>.
        /// None of the runtime's own steps at exit runs, since they can make a
        /// <c>DllImport</c> call. Returns only when ExitProcess could not be
        /// found.</summary>
        internal static void ExitNow(int code)
        {
            if (pExitProcess != IntPtr.Zero) ((delegate* unmanaged<uint, void>)pExitProcess)(unchecked((uint)code));
        }

        internal static uint ProcessId()
            => pGetCurrentProcessId == IntPtr.Zero ? 0 : ((delegate* unmanaged<uint>)pGetCurrentProcessId)();

        internal static ulong TickCount()
            => pGetTickCount64 == IntPtr.Zero ? 0 : ((delegate* unmanaged<ulong>)pGetTickCount64)();

        // ── Rights ──

        private const uint TokenQuery = 0x0008;
        private const uint TokenRead = 0x00020008;
        private const int TokenElevation = 20;
        internal const uint HighIntegrityRid = 0x3000;
        internal const uint MediumIntegrityRid = 0x2000;

        // ── A copy without rights, for the failed start's message ──

        /// <summary>
        /// Starts <paramref name="exe"/> without this process's rights: with a
        /// restricted copy of its token whose Administrators membership is
        /// for denying only, with every privilege but the one to traverse
        /// folders taken out (CreateRestrictedToken, DISABLE_MAX_PRIVILEGE),
        /// at medium integrity. A restricted copy of the caller's own token
        /// needs no right to assign a token (Microsoft, CreateProcessAsUserW).
        /// What the copy loads from a folder others can change then runs with
        /// no more rights than those others have. The process handle, or zero
        /// when Windows refused.
        /// </summary>
        internal static IntPtr StartWithoutRights(string exe, string commandLine, string environmentBlock, string folder)
        {
            IntPtr pCreateRestrictedToken = Find(Advapi32, "CreateRestrictedToken");
            IntPtr pSetTokenInformation = Find(Advapi32, "SetTokenInformation");
            IntPtr pCreateProcessAsUserW = Find(Advapi32, "CreateProcessAsUserW");
            IntPtr pConvertStringSidToSidW = Find(Advapi32, "ConvertStringSidToSidW");
            IntPtr pGetLengthSid = Find(Advapi32, "GetLengthSid");
            if (pGetLengthSid == IntPtr.Zero || pCreateRestrictedToken == IntPtr.Zero || pSetTokenInformation == IntPtr.Zero || pCreateProcessAsUserW == IntPtr.Zero
                || pConvertStringSidToSidW == IntPtr.Zero || pOpenProcessToken == IntPtr.Zero || pLocalFree == IntPtr.Zero) return IntPtr.Zero;
            const uint TokenDuplicate = 0x0002, TokenAssignPrimary = 0x0001, TokenAdjustDefault = 0x0080;
            IntPtr own = IntPtr.Zero, restricted = IntPtr.Zero, administrators = IntPtr.Zero, localAdmin = IntPtr.Zero, medium = IntPtr.Zero;
            try
            {
                if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)pOpenProcessToken)(
                        CurrentProcess, TokenDuplicate | TokenAssignPrimary | TokenQuery | TokenAdjustDefault, &own) == 0) return IntPtr.Zero;
                var toSid = (delegate* unmanaged<char*, IntPtr*, int>)pConvertStringSidToSidW;
                fixed (char* a = "S-1-5-32-544")
                fixed (char* l = "S-1-5-114")
                fixed (char* m = "S-1-16-8192")
                {
                    if (toSid(a, &administrators) == 0 || toSid(l, &localAdmin) == 0 || toSid(m, &medium) == 0) return IntPtr.Zero;
                }
                // SID_AND_ATTRIBUTES: the SID, then the attributes, 16 bytes
                // on x64 and ARM64.
                IntPtr* disable = stackalloc IntPtr[4];
                disable[0] = administrators;
                disable[1] = IntPtr.Zero;
                disable[2] = localAdmin;
                disable[3] = IntPtr.Zero;
                const uint DisableMaxPrivilege = 0x1;
                if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint, void*, uint, void*, IntPtr*, int>)pCreateRestrictedToken)(
                        own, DisableMaxPrivilege, 2, disable, 0, null, 0, null, &restricted) == 0) return IntPtr.Zero;
                // TOKEN_MANDATORY_LABEL: the medium label's SID, then
                // SE_GROUP_INTEGRITY.
                const int TokenIntegrityLevel = 25;
                const uint GroupIntegrity = 0x20;
                IntPtr* label = stackalloc IntPtr[2];
                label[0] = medium;
                label[1] = (IntPtr)GroupIntegrity;
                uint labelSize = (uint)(2 * IntPtr.Size) + ((delegate* unmanaged<IntPtr, uint>)pGetLengthSid)(medium);
                if (((delegate* unmanaged<IntPtr, int, void*, uint, int>)pSetTokenInformation)(restricted, TokenIntegrityLevel, label, labelSize) == 0)
                    return IntPtr.Zero;

                GetStartupInfo(out var startup);
                startup.lpReserved = IntPtr.Zero;
                startup.cbReserved2 = 0;
                startup.lpReserved2 = IntPtr.Zero;
                char[] line = new char[commandLine.Length + 1];
                commandLine.CopyTo(0, line, 0, commandLine.Length);
                ProcessInformation process;
                fixed (char* e = exe)
                fixed (char* c = line)
                fixed (char* env = environmentBlock)
                fixed (char* f = folder)
                {
                    if (((delegate* unmanaged<IntPtr, char*, char*, void*, void*, int, uint, void*, char*, StartupInfo*, ProcessInformation*, int>)pCreateProcessAsUserW)(
                            restricted, e, c, null, null, 0, CreateUnicodeEnvironment, env, f, &startup, &process) == 0) return IntPtr.Zero;
                }
                Close(process.hThread);
                return process.hProcess;
            }
            finally
            {
                Close(restricted);
                Close(own);
                var free = (delegate* unmanaged<IntPtr, IntPtr>)pLocalFree;
                if (administrators != IntPtr.Zero) free(administrators);
                if (localAdmin != IntPtr.Zero) free(localAdmin);
                if (medium != IntPtr.Zero) free(medium);
            }
        }

        /// <summary>Waits for a process to end.</summary>
        internal static void WaitFor(IntPtr process)
        {
            IntPtr p = Find(Kernel32, "WaitForSingleObject");
            if (p != IntPtr.Zero && process != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint, uint>)p)(process, Infinite);
        }

        /// <summary>Whether this process runs with administrator rights or as
        /// SYSTEM: its token is elevated, as Environment.IsPrivilegedProcess
        /// reads it (Environment.Windows.cs, IsPrivilegedProcessCore), or its
        /// user is SYSTEM. False when the token cannot be read.</summary>
        internal static bool IsPrivileged() => ReadToken(out bool elevated, out bool system) && (elevated || system);

        /// <summary>The integrity level's RID of this process's token, such
        /// as 0x2000 for medium and 0x3000 for high, or 0 when it cannot be
        /// read.</summary>
        internal static uint IntegrityRid()
        {
            if (pOpenProcessToken == IntPtr.Zero || pGetTokenInformation == IntPtr.Zero) return 0;
            IntPtr token = IntPtr.Zero;
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)pOpenProcessToken)(CurrentProcess, TokenQuery, &token) == 0) return 0;
            try
            {
                const int TokenIntegrityLevel = 25;
                byte* label = stackalloc byte[128];
                uint returned = 0;
                if (((delegate* unmanaged<IntPtr, int, void*, uint, uint*, int>)pGetTokenInformation)(token, TokenIntegrityLevel, label, 128, &returned) == 0)
                    return 0;
                // TOKEN_MANDATORY_LABEL: a SID_AND_ATTRIBUTES whose SID is
                // S-1-16-<RID>, one subauthority after the 8-byte header.
                byte* sid = *(byte**)label;
                if (sid == null || sid[1] < 1) return 0;
                return *(uint*)(sid + 8 + 4 * (sid[1] - 1));
            }
            finally
            {
                Close(token);
            }
        }

        /// <summary>Whether this process runs as SYSTEM (S-1-5-18). False when
        /// the token cannot be read.</summary>
        internal static bool IsSystem() => ReadToken(out _, out bool system) && system;

        private static bool ReadToken(out bool elevated, out bool system)
        {
            elevated = system = false;
            if (pOpenProcessToken == IntPtr.Zero || pGetTokenInformation == IntPtr.Zero) return false;
            IntPtr token = IntPtr.Zero;
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)pOpenProcessToken)(CurrentProcess, TokenRead | TokenQuery, &token) == 0)
                return false;
            try
            {
                var query = (delegate* unmanaged<IntPtr, int, void*, uint, uint*, int>)pGetTokenInformation;
                uint value = 0, returned = 0;
                elevated = query(token, TokenElevation, &value, sizeof(uint), &returned) != 0 && value != 0
                           && IntegrityRid() >= HighIntegrityRid;
                // SYSTEM, as a give-back task runs, counts whatever its token
                // says of elevation: its SID is S-1-5-18.
                const int TokenUser = 1;
                byte* user = stackalloc byte[256];
                if (query(token, TokenUser, user, 256, &returned) == 0) return elevated;
                byte* sid = *(byte**)user;
                system = sid != null && sid[0] == 1 && sid[1] == 1 && sid[2] == 0 && sid[3] == 0 && sid[4] == 0 && sid[5] == 0
                         && sid[6] == 0 && sid[7] == 5 && sid[8] == 18 && sid[9] == 0 && sid[10] == 0 && sid[11] == 0;
                return true;
            }
            finally
            {
                Close(token);
            }
        }

        // ── The failed start's message ──

        /// <summary>This process's Windows session, or uint.MaxValue when it
        /// cannot be read.</summary>
        internal static uint SessionId()
        {
            IntPtr p = Find(Kernel32, "ProcessIdToSessionId");
            if (p == IntPtr.Zero) return uint.MaxValue;
            uint session = 0;
            return ((delegate* unmanaged<uint, uint*, int>)p)(ProcessId(), &session) != 0 ? session : uint.MaxValue;
        }

        /// <summary>Shows an error message box with one OK button, through
        /// System32's user32.dll loaded by its full path, and returns once it
        /// is closed. False when user32 or MessageBoxW could not be
        /// found.</summary>
        internal static bool ErrorBox(string text, string title)
        {
            if (System32.Length == 0 || !NativeLibrary.TryLoad(System32 + "user32.dll", out IntPtr user32)) return false;
            IntPtr p = Find(user32, "MessageBoxW");
            if (p == IntPtr.Zero) return false;
            const uint MbOk = 0x0, MbIconError = 0x10, MbSetForeground = 0x10000;
            fixed (char* t = text)
            fixed (char* c = title)
                return ((delegate* unmanaged<IntPtr, char*, char*, uint, int>)p)(IntPtr.Zero, t, c, MbOk | MbIconError | MbSetForeground) != 0;
        }

        // ── Files and folders ──

        internal const uint InvalidAttributes = 0xFFFFFFFF;
        internal const uint AttributeDirectory = 0x10;
        internal const uint AttributeReparsePoint = 0x400;

        internal static uint Attributes(string path)
        {
            if (pGetFileAttributesW == IntPtr.Zero) return InvalidAttributes;
            fixed (char* p = path) return ((delegate* unmanaged<char*, uint>)pGetFileAttributesW)(p);
        }

        /// <summary>Creates a folder with the access list
        /// <paramref name="sddl"/> states, in one step, so there is no moment
        /// in which it stands with another. False when it could not be
        /// made, as when the name is taken.</summary>
        internal static bool CreateFolder(string path, string sddl)
        {
            if (pCreateDirectoryW == IntPtr.Zero || pConvertStringSdToSd == IntPtr.Zero || pLocalFree == IntPtr.Zero) return false;
            IntPtr descriptor = IntPtr.Zero;
            const uint SddlRevision1 = 1;
            fixed (char* s = sddl)
            {
                if (((delegate* unmanaged<char*, uint, IntPtr*, uint*, int>)pConvertStringSdToSd)(s, SddlRevision1, &descriptor, null) == 0)
                    return false;
            }
            try
            {
                var sa = new SecurityAttributes { Length = sizeof(SecurityAttributes), Descriptor = descriptor, Inherit = 0 };
                fixed (char* p = path) return ((delegate* unmanaged<char*, SecurityAttributes*, int>)pCreateDirectoryW)(p, &sa) != 0;
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, IntPtr>)pLocalFree)(descriptor);
            }
        }

        /// <summary>Renames a file or folder, a link as the link itself.</summary>
        internal static bool Rename(string from, string to)
        {
            if (pMoveFileExW == IntPtr.Zero) return false;
            fixed (char* f = from)
            fixed (char* t = to)
                return ((delegate* unmanaged<char*, char*, uint, int>)pMoveFileExW)(f, t, 0) != 0;
        }

        private const uint ReadControl = 0x00020000;
        private const uint ShareAll = 0x7;
        private const uint OpenExisting = 3;
        private const uint FlagBackupSemantics = 0x02000000;
        private const uint FlagOpenReparsePoint = 0x00200000;
        private const int SeFileObject = 1;
        internal const uint OwnerInformation = 0x1;
        internal const uint DaclInformation = 0x4;

        /// <summary>
        /// The security descriptor of a folder in its binary form, read from
        /// the folder itself: a link is opened as the link, not what it points
        /// at. <paramref name="attributes"/> are the same object's, so what
        /// was judged and what was read are one. Null when it cannot be
        /// opened or read.
        /// </summary>
        internal static byte[] ReadDescriptor(string path, out uint attributes) => ReadDescriptor(path, true, out attributes);

        /// <summary>The same, with a link followed to what it points at when
        /// <paramref name="asLink"/> is false.</summary>
        internal static byte[] ReadDescriptor(string path, bool asLink, out uint attributes)
        {
            attributes = InvalidAttributes;
            if (pCreateFileW == IntPtr.Zero || pGetFileInformationByHandle == IntPtr.Zero || pGetSecurityInfo == IntPtr.Zero
                || pGetSecurityDescriptorLength == IntPtr.Zero || pLocalFree == IntPtr.Zero) return null;
            IntPtr handle;
            fixed (char* p = path)
                handle = ((delegate* unmanaged<char*, uint, uint, void*, uint, uint, IntPtr, IntPtr>)pCreateFileW)(
                    p, ReadControl, ShareAll, null, OpenExisting, FlagBackupSemantics | (asLink ? FlagOpenReparsePoint : 0), IntPtr.Zero);
            if (handle == InvalidHandle || handle == IntPtr.Zero) return null;
            try
            {
                FileInformation info;
                if (((delegate* unmanaged<IntPtr, FileInformation*, int>)pGetFileInformationByHandle)(handle, &info) == 0) return null;
                attributes = info.Attributes;
                IntPtr owner, group, dacl, sacl, descriptor = IntPtr.Zero;
                uint error = ((delegate* unmanaged<IntPtr, int, uint, IntPtr*, IntPtr*, IntPtr*, IntPtr*, IntPtr*, uint>)pGetSecurityInfo)(
                    handle, SeFileObject, OwnerInformation | DaclInformation, &owner, &group, &dacl, &sacl, &descriptor);
                if (error != 0 || descriptor == IntPtr.Zero) return null;
                try
                {
                    uint length = ((delegate* unmanaged<IntPtr, uint>)pGetSecurityDescriptorLength)(descriptor);
                    if (length == 0) return null;
                    byte[] bytes = new byte[length];
                    Marshal.Copy(descriptor, bytes, 0, (int)length);
                    return bytes;
                }
                finally
                {
                    ((delegate* unmanaged<IntPtr, IntPtr>)pLocalFree)(descriptor);
                }
            }
            finally
            {
                Close(handle);
            }
        }

        /// <summary>The full form of a path, as Windows makes it. Null when it
        /// cannot.</summary>
        internal static string FullPath(string path)
        {
            if (pGetFullPathNameW == IntPtr.Zero || string.IsNullOrEmpty(path)) return null;
            var call = (delegate* unmanaged<char*, uint, char*, char**, uint>)pGetFullPathNameW;
            fixed (char* p = path)
            {
                uint needed = call(p, 0, null, null);
                if (needed == 0) return null;
                char[] buffer = new char[needed];
                fixed (char* b = buffer)
                {
                    uint got = call(p, needed, b, null);
                    return got == 0 || got >= needed ? null : new string(b, 0, (int)got);
                }
            }
        }

        /// <summary>Whether two strings are the same, upper and lower case
        /// alike, as the file system compares names (Microsoft,
        /// CompareStringOrdinal).</summary>
        internal static bool SameName(string a, string b)
        {
            if (a == null || b == null) return false;
            if (pCompareStringOrdinal == IntPtr.Zero) return string.Equals(a, b, StringComparison.Ordinal);
            const int Equal = 2;
            fixed (char* x = a)
            fixed (char* y = b)
                return ((delegate* unmanaged<char*, int, char*, int, int, int>)pCompareStringOrdinal)(x, a.Length, y, b.Length, 1) == Equal;
        }

        // ── Registry ──

        private static readonly IntPtr LocalMachine = new IntPtr(unchecked((int)0x80000002));
        private const uint RrfRtRegSz = 0x00000002;
        private const uint RrfRtRegExpandSz = 0x00000004;
        private const uint RrfNoExpand = 0x10000000;

        /// <summary>A text value under HKEY_LOCAL_MACHINE as it is written,
        /// with no variable expanded. Null when it is not there.</summary>
        internal static string MachineText(string key, string value)
        {
            if (pRegGetValueW == IntPtr.Zero) return null;
            var call = (delegate* unmanaged<IntPtr, char*, char*, uint, uint*, void*, uint*, int>)pRegGetValueW;
            uint flags = RrfRtRegSz | RrfRtRegExpandSz | RrfNoExpand;
            fixed (char* k = key)
            fixed (char* v = value)
            {
                uint bytes = 0;
                if (call(LocalMachine, k, v, flags, null, null, &bytes) != 0 || bytes < 2) return null;
                char[] buffer = new char[bytes / 2 + 1];
                fixed (char* b = buffer)
                {
                    uint size = (uint)(buffer.Length * 2);
                    if (call(LocalMachine, k, v, flags, null, b, &size) != 0) return null;
                    int length = 0;
                    while (length < buffer.Length && b[length] != '\0') length++;
                    return new string(b, 0, length);
                }
            }
        }
    }
}
