using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Services
{
    /// <summary>
    /// In-app Bluetooth pairing for Nintendo Wii controllers (issue #116).
    ///
    /// Wii Remotes, the Nunchuk/Classic extensions, and the Wii U Pro
    /// Controller use a legacy Bluetooth pairing ceremony that the Windows
    /// pairing UI cannot drive. The PIN is six raw address bytes rather than
    /// an ASCII string, and WinRT's pairing API only accepts strings, so the
    /// only viable path is Win32 <c>bluetoothapis</c> with a legacy-PIN
    /// authentication callback. This follows Dolphin's
    /// Source/Core/Core/HW/WiimoteReal/IOWin.cpp, the canonical Windows
    /// reference for pairing Wiimotes through the OS stack without a registry
    /// link-key hack.
    ///
    /// Two pairing modes, matching the controller's two sync methods:
    ///  - SYNC button (red button under the battery cover). PIN is the host
    ///    radio's address, low byte first. Triggers full bonding, so the
    ///    controller reconnects on any button press after a disconnect. Pair
    ///    once, reconnect free from then on.
    ///  - 1+2 hold (temporary). PIN is the controller's own address, low byte
    ///    first. Per the WiiBrew spec the controller does not bond in this
    ///    mode and must be re-paired every session.
    ///
    /// Requires the process to be elevated. <c>BluetoothSetServiceState</c>
    /// writes the link key under SYSTEM privilege, which is why no manual
    /// registry edit is needed. PadForge always runs elevated.
    ///
    /// Once paired the controller enumerates as a HID gamepad and the bundled
    /// SDL3 fork's hidapi_wii driver surfaces it. PadForge's normal device
    /// pipeline maps it from there. This service does the OS-level pairing
    /// only.
    /// </summary>
    public sealed class WiiPairingService
    {
        /// <summary>Name prefix every Wii peripheral advertises over Bluetooth
        /// ("Nintendo RVL-CNT-01", "-TR", "-UC", and so on).</summary>
        private const string WiiNamePrefix = "Nintendo RVL-";

        /// <summary>Result of one inquiry-and-pair pass.</summary>
        public sealed class PairPassResult
        {
            /// <summary>Wii controllers seen in this inquiry pass (by name).</summary>
            public List<string> Found { get; } = new();

            /// <summary>Controllers this pass successfully bonded (by name).</summary>
            public List<string> Paired { get; } = new();

            /// <summary>Set when the radio could not be opened or queried. Null
            /// on success even when no controllers were found.</summary>
            public string Error { get; set; }
        }

        // Holds the legacy PIN for the in-flight device so the auth callback,
        // which the Bluetooth stack invokes on its own thread, can read it.
        // RunPairingPass bonds one device at a time, so a single field is safe.
        private byte[] _currentPin;
        private IntPtr _currentRadio;

        // The registered callback must outlive the native registration, so it
        // is held in a field to keep the GC from collecting the delegate.
        private BluetoothAuthCallbackEx _authCallback;

        /// <summary>
        /// Runs a single Bluetooth inquiry and attempts to bond every Wii
        /// controller currently in pairing mode. Blocks for the inquiry
        /// duration (about three seconds), so call it from a background thread.
        /// The caller loops over passes until a controller pairs or the user
        /// cancels.
        /// </summary>
        /// <param name="temporary">True to use the 1+2 temporary PIN (the
        /// controller's own address). False to use the SYNC-button PIN (the
        /// host address) which bonds persistently.</param>
        public PairPassResult RunPairingPass(bool temporary, CancellationToken ct)
        {
            var result = new PairPassResult();

            IntPtr hRadio = IntPtr.Zero;
            IntPtr hRadioFind = IntPtr.Zero;
            try
            {
                var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>()
                };
                hRadioFind = BluetoothFindFirstRadio(ref radioParams, out hRadio);
                if (hRadioFind == IntPtr.Zero || hRadio == IntPtr.Zero)
                {
                    result.Error = "no-radio";
                    return result;
                }

                var radioInfo = new BLUETOOTH_RADIO_INFO
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_RADIO_INFO>()
                };
                if (BluetoothGetRadioInfo(hRadio, ref radioInfo) != 0)
                {
                    result.Error = "radio-info";
                    return result;
                }

                _currentRadio = hRadio;

                var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                    fReturnAuthenticated = 0,
                    fReturnRemembered = 0,
                    fReturnUnknown = 1,
                    fReturnConnected = 0,
                    fIssueInquiry = 1,
                    cTimeoutMultiplier = 2, // about 2.5s of inquiry per pass
                    hRadio = hRadio
                };

                var deviceInfo = new BLUETOOTH_DEVICE_INFO
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>()
                };

                IntPtr hDevFind = BluetoothFindFirstDevice(ref search, ref deviceInfo);
                if (hDevFind == IntPtr.Zero)
                    return result; // inquiry ran, nothing matched

                try
                {
                    do
                    {
                        if (ct.IsCancellationRequested) break;

                        string name = deviceInfo.szName ?? string.Empty;
                        if (!name.StartsWith(WiiNamePrefix, StringComparison.OrdinalIgnoreCase))
                            continue;

                        result.Found.Add(name);

                        ulong pinSource = temporary ? deviceInfo.Address : radioInfo.address;
                        if (TryPairDevice(hRadio, deviceInfo, pinSource))
                            result.Paired.Add(name);
                    }
                    while (BluetoothFindNextDevice(hDevFind, ref deviceInfo));
                }
                finally
                {
                    BluetoothFindDeviceClose(hDevFind);
                }
            }
            catch (DllNotFoundException)
            {
                result.Error = "no-bluetooth-stack";
            }
            catch (Exception)
            {
                result.Error = "exception";
            }
            finally
            {
                _currentRadio = IntPtr.Zero;
                if (hRadio != IntPtr.Zero) CloseHandle(hRadio);
                if (hRadioFind != IntPtr.Zero) BluetoothFindRadioClose(hRadioFind);
            }

            return result;
        }

        /// <summary>
        /// Bonds one discovered controller. Clears any stale bond first, then
        /// installs the legacy-PIN auth callback and enables the HID service,
        /// which is the step that makes the controller remember the host.
        /// </summary>
        private bool TryPairDevice(IntPtr hRadio, BLUETOOTH_DEVICE_INFO device, ulong pinSource)
        {
            _currentPin = AddressToPin(pinSource);

            // A stale bond from a prior session can make the service-enable
            // reuse an old link key and fail. Forget the device first so the
            // PIN exchange runs clean. Best effort, errors ignored.
            var addr = new BLUETOOTH_ADDRESS { ullLong = device.Address };
            BluetoothRemoveDevice(ref addr);

            IntPtr hAuth = IntPtr.Zero;
            _authCallback = AuthCallback;
            try
            {
                uint reg = BluetoothRegisterForAuthenticationEx(
                    ref device, out hAuth, _authCallback, IntPtr.Zero);
                if (reg != 0) return false;

                var hidGuid = HumanInterfaceDeviceServiceClass_UUID;
                uint rc = BluetoothSetServiceState(
                    hRadio, ref device, ref hidGuid, BLUETOOTH_SERVICE_ENABLE);
                return rc == 0;
            }
            finally
            {
                if (hAuth != IntPtr.Zero) BluetoothUnregisterAuthentication(hAuth);
                _authCallback = null;
                _currentPin = null;
            }
        }

        /// <summary>
        /// Auth callback the Bluetooth stack invokes during bonding. Replies
        /// with the six-byte legacy PIN for the in-flight controller.
        /// </summary>
        private bool AuthCallback(IntPtr pvParam, IntPtr pParams)
        {
            byte[] pin = _currentPin;
            if (pin == null) return false;

            // deviceInfo is the first field of the callback params struct.
            var device = Marshal.PtrToStructure<BLUETOOTH_DEVICE_INFO>(pParams);

            var pin16 = new byte[16];
            Array.Copy(pin, pin16, Math.Min(pin.Length, 16));

            var response = new BLUETOOTH_AUTHENTICATE_RESPONSE
            {
                bthAddressRemote = device.Address,
                authMethod = BLUETOOTH_AUTHENTICATION_METHOD_LEGACY,
                pinInfo = new BLUETOOTH_PIN_INFO { pin = pin16, pinLength = (byte)Math.Min(pin.Length, 16) },
                negativeResponse = 0
            };

            uint rc = BluetoothSendAuthenticationResponseEx(_currentRadio, ref response);
            return rc == 0;
        }

        /// <summary>
        /// Builds the legacy PIN from a Bluetooth address: the six address
        /// bytes, least significant first. This is the "address in reverse byte
        /// order" the WiiBrew spec describes.
        /// </summary>
        private static byte[] AddressToPin(ulong address)
        {
            var pin = new byte[6];
            for (int i = 0; i < 6; i++)
                pin[i] = (byte)((address >> (8 * i)) & 0xFF);
            return pin;
        }

        // ─────────────────────────────────────────────
        //  Win32 bluetoothapis interop
        // ─────────────────────────────────────────────

        private const uint BLUETOOTH_SERVICE_ENABLE = 0x01;
        private const uint BLUETOOTH_AUTHENTICATION_METHOD_LEGACY = 1;

        // HumanInterfaceDeviceServiceClass_UUID {00001124-0000-1000-8000-00805F9B34FB}
        private static readonly Guid HumanInterfaceDeviceServiceClass_UUID =
            new Guid(0x00001124, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB);

        private delegate bool BluetoothAuthCallbackEx(IntPtr pvParam, IntPtr pParams);

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_FIND_RADIO_PARAMS
        {
            public uint dwSize;
        }

        // BLUETOOTH_ADDRESS is a packed union of a 48-bit address. The low six
        // bytes of the 64-bit value are the address, least significant first.
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLUETOOTH_ADDRESS
        {
            public ulong ullLong;
        }

        // Pack = 1 keeps the 48-bit address field tight against dwSize, matching
        // the native layout where BLUETOOTH_ADDRESS has byte alignment. Every
        // field after it is naturally aligned regardless, so the rest matches.
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_RADIO_INFO
        {
            public uint dwSize;
            public ulong address;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string szName;
            public uint ulClassofDevice;
            public ushort lmpSubversion;
            public ushort manufacturer;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_DEVICE_INFO
        {
            public uint dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public int fConnected;
            public int fRemembered;
            public int fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string szName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        // hRadio is pointer-sized, so this struct must keep default alignment
        // (not Pack = 1) for the trailing handle to land on its 8-byte slot.
        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public uint dwSize;
            public int fReturnAuthenticated;
            public int fReturnRemembered;
            public int fReturnUnknown;
            public int fReturnConnected;
            public int fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLUETOOTH_PIN_INFO
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] pin;
            public byte pinLength;
        }

        // Explicit layout models the native union: the PIN info sits at the
        // union offset (12) and the whole struct is 48 bytes.
        [StructLayout(LayoutKind.Explicit, Size = 48)]
        private struct BLUETOOTH_AUTHENTICATE_RESPONSE
        {
            [FieldOffset(0)] public ulong bthAddressRemote;
            [FieldOffset(8)] public uint authMethod;
            [FieldOffset(12)] public BLUETOOTH_PIN_INFO pinInfo;
            [FieldOffset(44)] public byte negativeResponse;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstRadio(
            ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("bthprops.cpl")]
        private static extern uint BluetoothGetRadioInfo(
            IntPtr hRadio, ref BLUETOOTH_RADIO_INFO pRadioInfo);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstDevice(
            ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindNextDevice(
            IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

        [DllImport("bthprops.cpl")]
        private static extern uint BluetoothRemoveDevice(ref BLUETOOTH_ADDRESS pAddress);

        [DllImport("bthprops.cpl")]
        private static extern uint BluetoothRegisterForAuthenticationEx(
            ref BLUETOOTH_DEVICE_INFO pbtdiIn, out IntPtr phRegHandleOut,
            BluetoothAuthCallbackEx pfnCallbackIn, IntPtr pvParam);

        [DllImport("bthprops.cpl")]
        private static extern bool BluetoothUnregisterAuthentication(IntPtr hRegHandle);

        [DllImport("bthprops.cpl")]
        private static extern uint BluetoothSendAuthenticationResponseEx(
            IntPtr hRadio, ref BLUETOOTH_AUTHENTICATE_RESPONSE pauthResponse);

        [DllImport("bthprops.cpl")]
        private static extern uint BluetoothSetServiceState(
            IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref Guid pGuidService,
            uint dwServiceFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
