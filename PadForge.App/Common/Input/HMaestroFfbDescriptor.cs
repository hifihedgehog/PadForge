namespace PadForge.Common.Input
{
    /// <summary>
    /// HID PID 1.0 Output Report IDs that <see cref="HMaestroFfbDecoder"/>
    /// dispatches on. The descriptor bytes themselves are emitted by
    /// HIDMaestro v1.1.41+'s <c>HidDescriptorBuilder.AddPidFfbBlock()</c>;
    /// we only need the IDs here so the decoder can switch on the report
    /// byte that arrives with each <c>HMOutputPacket</c>.
    /// </summary>
    internal static class HMaestroFfbDescriptor
    {
        public static class OutputReportId
        {
            public const byte SetEffect       = 0x11;
            public const byte SetCondition    = 0x13;
            public const byte SetPeriodic     = 0x14;
            public const byte SetConstantForce = 0x15;
            public const byte SetRampForce    = 0x16;
            public const byte EffectOperation = 0x1A;
            public const byte BlockFree       = 0x1B;
            public const byte DeviceControl   = 0x1C;
            public const byte DeviceGain      = 0x1D;
        }
    }
}
