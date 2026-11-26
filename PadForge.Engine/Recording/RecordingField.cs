namespace PadForge.Engine.Recording
{
    /// <summary>
    /// One kind of change a problem recording holds (#600). The capture
    /// hands each change over as a field, an index and up to three numbers,
    /// and <see cref="RecordingCodec"/> turns that into a token of a line and
    /// back. A field belongs to one layer of the file.
    ///
    /// <para>The numbers are never saved, only the tokens are, so members
    /// can be added anywhere.</para>
    /// </summary>
    public enum RecordingField : byte
    {
        None = 0,

        // ── I: what a device reports ──
        Axis,
        Slider,
        Hat,
        Button,
        Gyro,
        Accel,
        GyroAux,
        AccelAux,
        /// <summary>A finger's contact: whether it is down, and its contact
        /// number. The index is the touchpad times 16 plus the finger.</summary>
        TouchContact,
        /// <summary>A finger's place and pressure.</summary>
        TouchPosition,
        TouchClick,
        MidiNote,
        MidiCc,
        MidiCcUp,
        MidiCcDown,
        MidiPitchBend,
        /// <summary>An analog key's depth. The index is the key's code.</summary>
        AnalogKey,
        IrPointer,
        JoyConIr,
        RingCon,
        /// <summary>A Joy-Con 2's mouse counts of one pass. Counts are of
        /// that pass alone, so every pass with motion is written.</summary>
        JoyCon2Mouse,
        /// <summary>A mouse's raw counts of one pass, written like
        /// <see cref="JoyCon2Mouse"/>.</summary>
        MouseRaw,
        /// <summary>A mouse's wheel units of one pass, the wheel and the
        /// sideways wheel (#600), written like <see cref="MouseRaw"/>.</summary>
        MouseWheel,
        CapSense,
        NfcTag,
        Battery,
        /// <summary>The device came online, or went offline with a
        /// reason (<see cref="RecordingCodec.OfflineReason"/>).</summary>
        Online,
        /// <summary>A part of the state's shape: how many touchpads, how
        /// many fingers each takes, and which optional parts exist. The
        /// index says which (<see cref="RecordingCodec.Shape"/>).</summary>
        Shape,

        // ── O: what a virtual controller is handed ──
        OutType,
        GpButtons,
        GpLeftTrigger,
        GpRightTrigger,
        GpLeftX,
        GpLeftY,
        GpRightX,
        GpRightY,
        GpExtra,
        RawAxis,
        RawButtons,
        RawHat,
        KbmKeys,
        KbmMouseButtons,
        KbmMove,
        KbmScroll,
        KbmAbsolute,
        /// <summary>Exact mouse counts of one pass, written on every pass
        /// that has any.</summary>
        KbmFlick,
        KbmGyro,
        KbmTouch,
        KbmCoast,
        MidiOutCc,
        MidiOutNote,
        VrHand,
        VrStick,
        OutTouch,
        OutTouchClick,
        OutGyro,
        OutAccel,

        // ── S: whether it went out ──
        Sent,

        // ── F: what a game sent back, and what reached a device ──
        /// <summary>The rumble a game asked of a virtual controller.</summary>
        GameRumble,
        /// <summary>The rumble last written to a device.</summary>
        DeviceRumble,

        // ── T: timing ──
        TimingPasses,
        TimingWork,
        TimingLate,
        TimingStep,
        TimingStall,
        TimingGap,
        TimingRest,
        /// <summary>A device call that took long (#600): the device is the
        /// subject, A the milliseconds, B what the call was
        /// (<see cref="RecordingCodec.CallKind"/>).</summary>
        TimingCall,

        // ── F: force effects (#600) ──
        /// <summary>What force updates asked of a device in the last second:
        /// A the updates, B the effects made, C the effects started again.
        /// Written when the counts change.</summary>
        ForceCounts,

        // ── M: a marker ──
        /// <summary>A marker set from the poll thread, by a macro.</summary>
        Marker,
    }
}
