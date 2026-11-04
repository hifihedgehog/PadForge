using System;
using System.Security.Cryptography;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// A device row for one lit device that no mouse or keyboard row reaches
    /// (#494, #557): a headset, a mousepad, a headset stand, a dock, a light
    /// strip, a speaker, a monitor or a chassis light, found as a HID
    /// LampArray, a Razer or SteelSeries device, a device of the shared
    /// worker or a Logitech HID++ device. Without a row it could not be
    /// assigned, so it gets this one. It has no inputs. Assigned to a
    /// virtual controller it takes that controller's lighting through its
    /// Lighting tab like any other lit device, and its Key Colors card where
    /// its unit knows its lights one by one.
    ///
    /// <para><see cref="PeripheralOutputRow"/>'s shape: a synthetic
    /// <see cref="ISdlInputDevice"/> registered by the same Step 1 phase.
    /// Where a vendor row stands for a vendor's software and takes every
    /// path no device claims, this stands for one unit and holds that unit's
    /// path alone. It opens once the link pass has found the unit unreached
    /// for <see cref="PeripheralLinker.UnitRowHoldMs"/> and retires when the
    /// unit leaves or a row of its own reaches it
    /// (<see cref="PeripheralOutputs.UnitRows"/>).</para>
    ///
    /// <para>Its ids are hashed from the unit's family and key, the
    /// interface path of the unit's collection, so the same device on the
    /// same port is the same row in every session and keeps its assignment.
    /// The product id is hashed from the same identity, never from the
    /// model: a record found by product alone is adopted by the next device
    /// of that product (InputManager.FindOrCreateUserDevice), and two lit
    /// units would then trade assignments.</para>
    /// </summary>
    internal sealed class PeripheralUnitRow : ISdlInputDevice, IOutputOnlyRow
    {
        // "PF", the synthetic vendor the vendor rows use, and "LU", a lit
        // unit, for every unit row: the ids below tell them apart.
        private const ushort SyntheticVendorId = 0x5046;
        internal const ushort UnitProductId = 0x4C55;

        private volatile bool _disposed;
        private PooledInputStatePair _statePool;

        /// <summary>The unit this row stands for.</summary>
        public OutputFamily Family { get; }
        public string Key { get; }
        public OutputPath Unit => new(Family, Key);

        public PeripheralUnitRow(DirectUnit unit)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            Family = unit.Family;
            Key = unit.Key ?? string.Empty;
            Name = NameFor(unit);
            string identity = IdentityTag(Family, Key);
            InstanceGuid = Md5Guid(identity);
            ProductGuid = Md5Guid(identity + "-product");
            DevicePath = "lightunit://" + InstanceGuid.ToString("N");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
        }

        /// <summary>The row's id for a unit, stable across sessions.</summary>
        public static Guid IdentityFor(OutputFamily family, string key) => Md5Guid(IdentityTag(family, key));

        /// <summary>The family by its number, which the enum's append-only
        /// rule keeps, then the key in one case: Windows compares interface
        /// paths without regard to case, and a path that came back in
        /// another case must not read as another device.</summary>
        private static string IdentityTag(OutputFamily family, string key)
            => "pfunit:" + ((int)family).ToString(System.Globalization.CultureInfo.InvariantCulture)
               + ":" + (key ?? string.Empty).ToUpperInvariant();

        /// <summary>The row's name: the product name the unit's backend
        /// knows (<see cref="DirectUnit.Name"/>), the one the device gave
        /// for a LampArray or HID++ unit, the product table's for a Razer
        /// unit and OpenRGB's for a unit of the shared worker. Where there
        /// is none, what kind of unit it is and its vendor and product ID,
        /// the way a vendor collection with no name is listed
        /// (VendorHidRuntime.Probe), and the vendor ID alone for a unit
        /// whose product ID its backend does not know (0), a HID++ device
        /// behind a receiver. A product name is not translated, and neither
        /// is the other form.</summary>
        public static string NameFor(DirectUnit unit)
        {
            if (!string.IsNullOrWhiteSpace(unit.Name)) return unit.Name.Trim();
            string kind = unit.Family == OutputFamily.LampArrayUnit ? "LampArray" : "Device";
            return unit.ProductId == 0 ? $"{kind} {unit.VendorId:X4}" : $"{kind} {unit.VendorId:X4}:{unit.ProductId:X4}";
        }

        // ─── ISdlInputDevice identity / capabilities ───
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => 0;
        public int RawButtonCount => 0;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => Array.Empty<int>();
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => false;
        public bool HasRumbleTriggers => false;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public bool HasTouchpad => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public bool IsAttached => !_disposed;
        public ushort VendorId => SyntheticVendorId;
        public ushort ProductId => UnitProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.PeripheralLighting;

        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        public DeviceObjectItem[] GetDeviceObjects() => Array.Empty<DeviceObjectItem>();

        /// <summary>A rest state every poll: the row has nothing to read.</summary>
        public CustomInputState GetCurrentState(bool forceRaw = false)
            => _disposed ? null : _statePool.Next();

        public void Dispose() => _disposed = true;

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
