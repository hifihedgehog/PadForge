using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Describes a force feedback effect supported by a device.
    /// Under SDL3, rumble is the primary (and typically only) effect type.
    /// This class is kept for UI display and settings compatibility, but the
    /// actual force feedback is implemented via <see cref="SdlDeviceWrapper.SetRumble"/>.
    /// 
    /// Replaces the former DeviceEffectItem that depended on SharpDX.DirectInput EffectInfo.
    /// </summary>
    public class DeviceEffectItem
    {
        // ─────────────────────────────────────────────
        //  Well-known effect GUIDs (from DirectInput)
        // ─────────────────────────────────────────────

        /// <summary>GUID_ConstantForce — {13541C20-8E33-11D0-9AD0-00A0C9A06E35}</summary>
        public static readonly Guid ConstantForce = new Guid("13541C20-8E33-11D0-9AD0-00A0C9A06E35");

        /// <summary>GUID_Square — {13541C22-8E33-11D0-9AD0-00A0C9A06E35}</summary>
        public static readonly Guid Square = new Guid("13541C22-8E33-11D0-9AD0-00A0C9A06E35");

        /// <summary>GUID_Sine — {13541C23-8E33-11D0-9AD0-00A0C9A06E35}</summary>
        public static readonly Guid Sine = new Guid("13541C23-8E33-11D0-9AD0-00A0C9A06E35");

        /// <summary>GUID_Triangle — {13541C24-8E33-11D0-9AD0-00A0C9A06E35}</summary>
        public static readonly Guid Triangle = new Guid("13541C24-8E33-11D0-9AD0-00A0C9A06E35");

        /// <summary>GUID_Spring — {13541C27-8E33-11D0-9AD0-00A0C9A06E35}</summary>
        public static readonly Guid Spring = new Guid("13541C27-8E33-11D0-9AD0-00A0C9A06E35");

        /// <summary>GUID_Damper — {13541C28-8E33-11D0-9AD0-00A0C9A06E35}</summary>
        public static readonly Guid Damper = new Guid("13541C28-8E33-11D0-9AD0-00A0C9A06E35");

        /// <summary>Synthetic GUID for SDL rumble (no DirectInput equivalent).</summary>
        public static readonly Guid SdlRumble = new Guid("53444C52-554D-424C-0000-000000000000"); // "SDLRUMBL"

        // ─────────────────────────────────────────────
        //  Instance properties
        // ─────────────────────────────────────────────

        /// <summary>
        /// GUID identifying the effect type.
        /// </summary>
        public Guid EffectGuid { get; set; } = Guid.Empty;

        /// <summary>
        /// Human-readable name of the effect (e.g., "Constant Force", "Rumble").
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Parameter flags indicating which parameters the effect supports.
        /// For SDL rumble this is typically <see cref="EffectParameterFlags.None"/>.
        /// </summary>
        public EffectParameterFlags Parameters { get; set; } = EffectParameterFlags.None;

        // ─────────────────────────────────────────────
        //  Factory methods
        // ─────────────────────────────────────────────

        /// <summary>
        /// Creates a <see cref="DeviceEffectItem"/> representing SDL rumble support.
        /// </summary>
        public static DeviceEffectItem CreateRumbleEffect()
        {
            return new DeviceEffectItem
            {
                EffectGuid = SdlRumble,
                Name = "Rumble",
                Parameters = EffectParameterFlags.None
            };
        }

        /// <summary>
        /// Returns a display-friendly string.
        /// </summary>
        public override string ToString()
        {
            return Name;
        }
    }
}
