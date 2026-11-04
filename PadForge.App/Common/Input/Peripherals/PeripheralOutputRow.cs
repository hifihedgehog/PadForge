using System;
using System.Security.Cryptography;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>The rows a vendor's software stands behind (#494).
    /// APPEND-ONLY: the value is never persisted, but the identity strings
    /// below are, through each row's InstanceGuid.</summary>
    internal enum PeripheralRowKind
    {
        /// <summary>Every Razer Chroma device Synapse lights, through its six
        /// categories.</summary>
        RazerChroma,
        /// <summary>Every Logitech device G HUB lights through the LED SDK,
        /// through its device types.</summary>
        LogitechLightsync,
        /// <summary>Razer Sensa HD devices through the Interhaptics engine
        /// (#374).</summary>
        RazerSensa,
        /// <summary>The SteelSeries mice, keyboards and headsets GG lights
        /// through GameSense's general device types. GameSense has no
        /// mousepad type: the QcK Prism pads answer only its zone-count types
        /// (gamesense-sdk standard-zones.md:29 and 37), which no row binds.</summary>
        SteelSeriesGG,
        /// <summary>Every ASUS device Armoury Crate lights through the Aura
        /// SDK, through its kinds (#557).</summary>
        AsusAura,
        /// <summary>Every MSI device Mystic Light lights through its SDK,
        /// through its device types (#557).</summary>
        MsiMysticLight,
        /// <summary>Every Corsair device iCUE lights through its SDK, through
        /// its device types, the mousepads and headset stands no input row
        /// stands for among them (#557).</summary>
        CorsairIcue,
    }

    /// <summary>A row with no input of its own (#494): a vendor row
    /// (<see cref="PeripheralOutputRow"/>), which stands for a vendor's
    /// software, or a unit row (<see cref="PeripheralUnitRow"/>), which
    /// stands for one lit device no mouse or keyboard row reaches. Such a
    /// row is assigned for its output alone, so it lights with no Control
    /// This Device's Lighting switch, and it is never shared with a linked
    /// PC, which would get a device that does nothing. Every place that
    /// asks whether a row is one asks this, and the link table carries the
    /// answer for a caller that holds only the row's id
    /// (<see cref="DeviceLinks.OutputOnly"/>).</summary>
    internal interface IOutputOnlyRow
    {
    }

    /// <summary>
    /// A device row for output a vendor's software delivers to devices
    /// PadForge does not read: a Razer headset or mousepad, a Logitech
    /// speaker, a SteelSeries headset, a Sensa HD controller (#494). It has no inputs. Assigned to a
    /// virtual controller, it takes that controller's lighting through its
    /// Lighting tab or its rumble through its Force Feedback tab, like any
    /// other device.
    ///
    /// <para><see cref="LogitechGKeysDevice"/>'s shape: a synthetic
    /// <see cref="ISdlInputDevice"/> with a fixed path and an id hashed from
    /// it, registered by a Step 1 phase. The row opens whenever the vendor's
    /// software is installed, so it can be assigned before that software
    /// runs. Whether it answers shows on the row's tab.</para>
    /// </summary>
    internal sealed class PeripheralOutputRow : ISdlInputDevice, IOutputOnlyRow
    {
        // "PF" plus a two-letter tag, the synthetic identity convention the
        // handheld and G-key rows set.
        private const ushort SyntheticVendorId = 0x5046;

        private volatile bool _disposed;
        private PooledInputStatePair _statePool;

        public PeripheralRowKind Kind { get; }

        // Each row's identity tag, hashed once. The ids persist through
        // every assignment and profile that names a row, and the Razer Sensa
        // worker reads its row's level by id on every tick.
        private const string ChromaTag = "pfrazerchroma";
        private const string LightsyncTag = "pflogilightsync";
        private const string SensaTag = "pfrazersensa";
        private const string SteelSeriesTag = "pfsteelseriesgg";
        private const string AuraTag = "pfasusaura";
        private const string MysticLightTag = "pfmsimysticlight";
        private const string IcueTag = "pfcorsairicue";
        private static readonly Guid ChromaId = Md5Guid(ChromaTag);
        private static readonly Guid LightsyncId = Md5Guid(LightsyncTag);
        private static readonly Guid SensaId = Md5Guid(SensaTag);
        private static readonly Guid SteelSeriesId = Md5Guid(SteelSeriesTag);
        private static readonly Guid AuraId = Md5Guid(AuraTag);
        private static readonly Guid MysticLightId = Md5Guid(MysticLightTag);
        private static readonly Guid IcueId = Md5Guid(IcueTag);

        /// <summary>A row for one kind. Every kind has its own arm here and
        /// in <see cref="NameFor"/>, <see cref="IdentityFor"/> and
        /// <see cref="IsLightingRow"/>, and a kind none of them lists is
        /// refused, so a kind appended later can never take another row's
        /// path, name or id.</summary>
        public PeripheralOutputRow(PeripheralRowKind kind)
        {
            Kind = kind;
            Name = NameFor(kind);
            (DevicePath, ProductId, string identity) = kind switch
            {
                PeripheralRowKind.RazerChroma => ("razerchroma://local", (ushort)0x4348, ChromaTag),
                PeripheralRowKind.LogitechLightsync => ("logilightsync://local", (ushort)0x4C53, LightsyncTag),
                PeripheralRowKind.RazerSensa => ("razersensa://local", (ushort)0x5345, SensaTag),
                PeripheralRowKind.SteelSeriesGG => ("steelseriesgg://local", (ushort)0x5347, SteelSeriesTag),
                PeripheralRowKind.AsusAura => ("asusaura://local", (ushort)0x4155, AuraTag),
                PeripheralRowKind.MsiMysticLight => ("msimysticlight://local", (ushort)0x4D4C, MysticLightTag),
                PeripheralRowKind.CorsairIcue => ("corsairicue://local", (ushort)0x4943, IcueTag),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            InstanceGuid = IdentityFor(kind);
            ProductGuid = Md5Guid(identity + "-product");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
        }

        /// <summary>The row's name, a product name no locale translates.</summary>
        public static string NameFor(PeripheralRowKind kind) => kind switch
        {
            PeripheralRowKind.RazerChroma => "Razer Chroma",
            PeripheralRowKind.LogitechLightsync => "Logitech LIGHTSYNC",
            PeripheralRowKind.RazerSensa => "Razer Sensa",
            PeripheralRowKind.SteelSeriesGG => "SteelSeries GG",
            PeripheralRowKind.AsusAura => "ASUS Aura",
            PeripheralRowKind.MsiMysticLight => "MSI Mystic Light",
            PeripheralRowKind.CorsairIcue => "Corsair iCUE",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        /// <summary>The kind of the row with this id, or null for any other
        /// device.</summary>
        public static PeripheralRowKind? KindOf(Guid id)
        {
            foreach (PeripheralRowKind kind in Enum.GetValues(typeof(PeripheralRowKind)))
                if (IdentityFor(kind) == id) return kind;
            return null;
        }

        /// <summary>The row's id, stable across sessions: the migration of
        /// the old global switches assigns it before the row first opens.</summary>
        public static Guid IdentityFor(PeripheralRowKind kind) => kind switch
        {
            PeripheralRowKind.RazerChroma => ChromaId,
            PeripheralRowKind.LogitechLightsync => LightsyncId,
            PeripheralRowKind.RazerSensa => SensaId,
            PeripheralRowKind.SteelSeriesGG => SteelSeriesId,
            PeripheralRowKind.AsusAura => AuraId,
            PeripheralRowKind.MsiMysticLight => MysticLightId,
            PeripheralRowKind.CorsairIcue => IcueId,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        public static bool IsLightingRow(PeripheralRowKind kind) => kind switch
        {
            PeripheralRowKind.RazerChroma or PeripheralRowKind.LogitechLightsync or PeripheralRowKind.SteelSeriesGG
                or PeripheralRowKind.AsusAura or PeripheralRowKind.MsiMysticLight
                or PeripheralRowKind.CorsairIcue => true,
            PeripheralRowKind.RazerSensa => false,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

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
        public ushort ProductId { get; }
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => IsLightingRow(Kind)
            ? InputDeviceType.PeripheralLighting
            : InputDeviceType.PeripheralHaptics;

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
