using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The firmware a Bluetooth LE controller reports in its own Device
    /// Information service (GATT service 0x180A, Firmware Revision String
    /// 0x2A26, UTF-8), for a device whose SDL driver reports no firmware
    /// version. An Xbox pad on Bluetooth reaches SDL through XInput, which
    /// builds its joystick from the vendor id, product id and product version
    /// alone (SDL_xinputjoystick.c:187), so the Device Dossier showed no
    /// FIRMWARE line for it. A Series X pad on Bluetooth LE answers
    /// "5.23.6.0", read on the reporting hardware, where its product version
    /// is 0x0523. Windows keeps a Bluetooth device's address, model and
    /// product version as device properties but not its firmware revision
    /// (km/bthguid.h:42-67), so the revision is read over GATT.
    ///
    /// <para>The pad is found exactly, never by vendor and product id, so two
    /// pads of one model each read their own. An XInput slot's interface
    /// comes from OpenXInput's identity call (OpenXInputGetDeviceIdentityV1,
    /// OpenXinput.cpp:4103), checked against the joystick's vendor and
    /// product id. A HID path is the device's own. Windows names the
    /// interface's device instance (DEVPKEY_Device_InstanceId, devpkey.h:86),
    /// and the walk up its PnP parents to the Bluetooth address
    /// (DEVPKEY_Bluetooth_DeviceAddress, km/bthguid.h:44) is
    /// BluetoothBatteryService's.</para>
    ///
    /// <para>The read runs off the UI thread, once for each SDL joystick,
    /// since a reconnect opens a new one. While the pad is connected it is
    /// asked first, so a pad updated and reconnected reads its new version.
    /// Windows' cached value answers when the pad does not, and a paired pad
    /// that is off is never sent a request. A read that fails is tried again
    /// 30 s later, three tries in all. The test host is fenced off with
    /// BluetoothLinkHelper's flag, as for every call that reaches the
    /// radio.</para>
    /// </summary>
    internal static class BluetoothFirmwareService
    {
        /// <summary>Test seam: answers in place of the read when set. Per
        /// thread, so a test that sets it changes no other test's
        /// answer.</summary>
        [ThreadStatic]
        internal static Func<SdlDeviceWrapper, string> AnswerForTest;

        private const int MaxAttempts = 3;
        private const int RetryMs = 30_000;
        private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);

        private static readonly Guid DeviceInformationService = new("0000180a-0000-1000-8000-00805f9b34fb");
        private static readonly Guid FirmwareRevisionString = new("00002a26-0000-1000-8000-00805f9b34fb");

        /// <summary>One joystick's read. Value is null while unknown and
        /// empty when the device has none.</summary>
        private sealed class Read
        {
            public string Value;
            public int Attempts;
            public long NextTick;
            public int Running;
        }

        // Keyed by SDL joystick instance, which SDL never hands out twice in
        // one process, across SDL_Quit too (SDL_GetNextObjectID's static
        // counter, SDL_utils.c:136-145).
        private static readonly ConcurrentDictionary<uint, Read> s_reads = new();

        /// <summary>The firmware revision the device reports, or null while it
        /// is unknown or when the device has none. The first ask for a
        /// joystick starts the read off the UI thread.</summary>
        public static string TryGet(SdlDeviceWrapper sdl)
        {
            var answer = AnswerForTest;
            if (answer != null) return answer(sdl);
            if (sdl == null || sdl.SdlInstanceId == 0) return null;

            var read = s_reads.GetOrAdd(sdl.SdlInstanceId, _ => new Read());
            string value = Volatile.Read(ref read.Value);
            if (value != null) return value.Length > 0 ? value : null;
            if (read.Attempts < MaxAttempts
                && Environment.TickCount64 >= Volatile.Read(ref read.NextTick)
                && Interlocked.CompareExchange(ref read.Running, 1, 0) == 0)
            {
                read.Attempts++;
                string path = sdl.SdlDevicePath;
                ushort vendorId = sdl.VendorId, productId = sdl.ProductId;
                _ = Task.Run(() => RunAsync(read, path, vendorId, productId));
            }
            return null;
        }

        private static async Task RunAsync(Read read, string path, ushort vendorId, ushort productId)
        {
            bool done;
            string text = null;
            try
            {
                var (address, final) = AddressOf(path, vendorId, productId);
                if (address == 0)
                {
                    done = final;
                }
                else
                {
                    (text, done) = await ReadRevisionAsync(address).ConfigureAwait(false);
                }
            }
            catch
            {
                done = false;
            }

            if (text != null || done || read.Attempts >= MaxAttempts)
                Volatile.Write(ref read.Value, text ?? string.Empty);
            else
                Volatile.Write(ref read.NextTick, Environment.TickCount64 + RetryMs);
            Volatile.Write(ref read.Running, 0);
        }

        // ── Which device ──

        /// <summary>The Bluetooth address behind an SDL joystick path, or 0.
        /// Final is false when a later try can answer differently: OpenXInput
        /// busy, or the slot's interface naming another pad while SDL catches
        /// up with a reshuffle.</summary>
        private static (ulong Address, bool Final) AddressOf(string path, ushort vendorId, ushort productId)
        {
            string iface;
            if (BluetoothLinkHelper.TryParseXInputSlot(path, out uint slot))
            {
                var (found, final) = XInputInterface(slot);
                if (found == null) return (0, final);
                if (!HidHideController.NamesVidPid(found, vendorId, productId)) return (0, false);
                iface = found;
            }
            else if (path != null && path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                iface = path;
            }
            else
            {
                return (0, true);
            }
            string instanceId = InterfaceInstanceId(iface);
            return (instanceId == null ? 0 : WalkForAddress(instanceId), true);
        }

        /// <summary>The interface OpenXInput serves an XInput slot from. Its
        /// identity call does no enumeration or device I/O and answers
        /// ERROR_BUSY instead of waiting on its lock (OpenXinput.h:476-478).
        /// Microsoft's xinput1_4.dll has no such export, which is final.</summary>
        private static (string Path, bool Final) XInputInterface(uint slot)
        {
            try
            {
                var identity = new OpenXInputIdentity { cbSize = (uint)Marshal.SizeOf<OpenXInputIdentity>() };
                var buffer = new char[512];
                uint rc = OpenXInputGetDeviceIdentityV1(slot, ref identity, buffer, (uint)buffer.Length);
                if (rc == ERROR_INSUFFICIENT_BUFFER && identity.requiredCch > buffer.Length && identity.requiredCch <= 32768)
                {
                    buffer = new char[identity.requiredCch];
                    rc = OpenXInputGetDeviceIdentityV1(slot, ref identity, buffer, (uint)buffer.Length);
                }
                if (rc == ERROR_SUCCESS && identity.requiredCch > 1 && identity.requiredCch <= buffer.Length)
                    return (new string(buffer, 0, (int)identity.requiredCch - 1), true);
                return (null, rc == ERROR_NOT_SUPPORTED);
            }
            catch (EntryPointNotFoundException) { return (null, true); }
            catch (DllNotFoundException) { return (null, true); }
        }

        /// <summary>The device instance an interface belongs to, as Windows
        /// names it.</summary>
        private static string InterfaceInstanceId(string iface)
        {
            var buf = new byte[1024];
            uint size = (uint)buf.Length;
            if (CM_Get_Device_Interface_PropertyW(iface, in DEVPKEY_Device_InstanceId,
                    out uint type, buf, ref size, 0) != CR_SUCCESS
                || type != DEVPROP_TYPE_STRING || size < 4)
            {
                return null;
            }
            string id = Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
            return id.Length > 0 ? id : null;
        }

        /// <summary>Walks up the instance's own PnP ancestry to the first
        /// Bluetooth address, or 0. The address sits on the BTHLEDEVICE
        /// service node one hop above an LE pad's HID node and on the BTHLE
        /// node above that, read on the reporting hardware. 8 bounds the
        /// walk, as in BluetoothBatteryService.</summary>
        private static ulong WalkForAddress(string instanceId)
        {
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS) return 0;
            var buf = new byte[64];
            for (int hop = 0; hop < 8; hop++)
            {
                uint size = (uint)buf.Length;
                if (CM_Get_DevNode_PropertyW(devInst, in DEVPKEY_Bluetooth_DeviceAddress,
                        out uint type, buf, ref size, 0) == CR_SUCCESS
                    && type == DEVPROP_TYPE_STRING)
                {
                    return ParseAddress(Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0'));
                }
                if (CM_Get_Parent(out uint parent, devInst, 0) != CR_SUCCESS) break;
                devInst = parent;
            }
            return 0;
        }

        /// <summary>A Bluetooth address as Windows writes it, twelve hex
        /// digits ("a88c3e2c8a96"), separators allowed, or 0.</summary>
        internal static ulong ParseAddress(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            string hex = text.Replace(":", "").Replace("-", "").Trim();
            return hex.Length == 12
                   && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture, out ulong address)
                ? address : 0;
        }

        // ── The read ──

        /// <summary>The device's Firmware Revision String. Final is false when
        /// the pad could not be reached and the cache had no answer.</summary>
        private static async Task<(string Text, bool Final)> ReadRevisionAsync(ulong address)
        {
            if (BluetoothLinkHelper.OffLimitsForTest) return (null, true);
            BluetoothLEDevice device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                .AsTask().WaitAsync(StepTimeout).ConfigureAwait(false);
            if (device == null) return (null, true);
            try
            {
                if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    var fromPad = await ReadRevisionAsync(device, BluetoothCacheMode.Uncached).ConfigureAwait(false);
                    if (fromPad.Final) return fromPad;
                }
                return await ReadRevisionAsync(device, BluetoothCacheMode.Cached).ConfigureAwait(false);
            }
            finally
            {
                device.Dispose();
            }
        }

        /// <summary>One read in one cache mode. A pad that answers without the
        /// service or the characteristic has none, which is final. An empty
        /// cache is not.</summary>
        private static async Task<(string Text, bool Final)> ReadRevisionAsync(BluetoothLEDevice device, BluetoothCacheMode mode)
        {
            bool fromPad = mode == BluetoothCacheMode.Uncached;
            var services = await device.GetGattServicesForUuidAsync(DeviceInformationService, mode)
                .AsTask().WaitAsync(StepTimeout).ConfigureAwait(false);
            if (services.Status != GattCommunicationStatus.Success) return (null, false);
            try
            {
                if (services.Services.Count == 0) return (null, fromPad);
                var characteristics = await services.Services[0].GetCharacteristicsForUuidAsync(FirmwareRevisionString, mode)
                    .AsTask().WaitAsync(StepTimeout).ConfigureAwait(false);
                if (characteristics.Status != GattCommunicationStatus.Success) return (null, false);
                if (characteristics.Characteristics.Count == 0) return (null, fromPad);
                var result = await characteristics.Characteristics[0].ReadValueAsync(mode)
                    .AsTask().WaitAsync(StepTimeout).ConfigureAwait(false);
                if (result.Status != GattCommunicationStatus.Success || result.Value == null) return (null, false);
                var bytes = new byte[result.Value.Length];
                using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(result.Value))
                    reader.ReadBytes(bytes);
                return (Clean(bytes), true);
            }
            finally
            {
                foreach (var service in services.Services) service.Dispose();
            }
        }

        /// <summary>The revision as text: UTF-8 as the Device Information
        /// service defines it, trailing NULs and spaces cut, and null for
        /// anything empty, unprintable or longer than 64 characters.</summary>
        internal static string Clean(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            string text = Encoding.UTF8.GetString(bytes).TrimEnd('\0', ' ').Trim();
            if (text.Length == 0 || text.Length > 64) return null;
            foreach (char c in text)
                if (char.IsControl(c) || c == '\uFFFD') return null;
            return text;
        }

        // ── Native ──

        /// <summary>OPENXINPUT_DEVICE_IDENTITY_V1 (OpenXinput.h:463-470): 24
        /// bytes, generation at offset 8.</summary>
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

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern int CM_Locate_DevNodeW(
            out uint pdnDevInst, [MarshalAs(UnmanagedType.LPWStr)] string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(
            uint dnDevInst, in DEVPROPKEY propertyKey,
            out uint propertyType, byte[] propertyBuffer,
            ref uint propertyBufferSize, uint ulFlags);

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

        // km/bthguid.h:44
        private static readonly DEVPROPKEY DEVPKEY_Bluetooth_DeviceAddress =
            new() { fmtid = new Guid("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A"), pid = 1 };

        private const int CR_SUCCESS = 0;
        private const uint DEVPROP_TYPE_STRING = 0x00000012;
        private const uint ERROR_SUCCESS = 0;
        private const uint ERROR_NOT_SUPPORTED = 50;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    }
}
