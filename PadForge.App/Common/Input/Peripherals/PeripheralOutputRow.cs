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
    internal sealed class PeripheralOutputRow : ISdlInputDevice
    {
        // "PF" plus a two-letter tag, the synthetic identity convention the
        // handheld and G-key rows set.
        private const ushort SyntheticVendorId = 0x5046;

        private volatile bool _disposed;
        private PooledInputStatePair _statePool;

        public PeripheralRowKind Kind { get; }

        public PeripheralOutputRow(PeripheralRowKind kind)
        {
            Kind = kind;
            Name = NameFor(kind);
            (DevicePath, ProductId, string identity) = kind switch
            {
                PeripheralRowKind.RazerChroma => ("razerchroma://local", (ushort)0x4348, "pfrazerchroma"),
                PeripheralRowKind.LogitechLightsync => ("logilightsync://local", (ushort)0x4C53, "pflogilightsync"),
                PeripheralRowKind.SteelSeriesGG => ("steelseriesgg://local", (ushort)0x5347, "pfsteelseriesgg"),
                _ => ("razersensa://local", (ushort)0x5345, "pfrazersensa"),
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
            PeripheralRowKind.SteelSeriesGG => "SteelSeries GG",
            _ => "Razer Sensa",
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
        public static Guid IdentityFor(PeripheralRowKind kind) => Md5Guid(kind switch
        {
            PeripheralRowKind.RazerChroma => "pfrazerchroma",
            PeripheralRowKind.LogitechLightsync => "pflogilightsync",
            PeripheralRowKind.SteelSeriesGG => "pfsteelseriesgg",
            _ => "pfrazersensa",
        });

        public static bool IsLightingRow(PeripheralRowKind kind) => kind != PeripheralRowKind.RazerSensa;

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
