using System;
using System.Collections.Generic;
using HIDMaestro;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Decodes raw HID PID effect packets (delivered via HIDMaestro's
    /// HMOutputPacket on HMOutputSource.HidOutput) into per-effect state and
    /// aggregates running effects into a single <see cref="Vibration"/> for
    /// the physical FFB device. One instance per Extended virtual controller.
    ///
    /// Mirrors the v2 vJoy FfbCallback behavior: the dominant running effect
    /// drives <see cref="Vibration.Direction"/> and <see cref="Vibration.SignedMagnitude"/>;
    /// running effects are polar-split into left/right motor scalars for
    /// rumble-only physical devices; condition effects (spring/damper/etc.)
    /// pass through to <see cref="Vibration.ConditionAxes"/>.
    ///
    /// Report IDs match the layout in <see cref="HMaestroFfbDescriptor"/>:
    /// 0x11 Set Effect, 0x13 Set Condition, 0x14 Set Periodic, 0x15 Set Constant,
    /// 0x16 Set Ramp, 0x1A Effect Operation, 0x1B Block Free, 0x1C Device Control,
    /// 0x1D Device Gain.
    /// </summary>
    internal sealed class HMaestroFfbDecoder
    {
        private const byte MaxSimultaneousEffects = 16;
        private const ushort RamPoolSize = 0xFFFF;

        private readonly Dictionary<byte, EffectState> _effects = new();
        private byte _deviceGain = 255;
        private readonly object _lock = new();
        private readonly HMController _controller;
        private byte _lastEbi;
        private PidStateFlags _stateFlags = PidStateFlags.ActuatorsEnabled | PidStateFlags.ActuatorPower;

        public HMaestroFfbDecoder(HMController controller)
        {
            _controller = controller;
        }

        /// <summary>Publish initial PID state. Call from HMaestroVirtualController.Connect()
        /// AFTER the controller is constructed. Without these initial publishes the SDK
        /// returns STATUS_NO_SUCH_DEVICE for the Pool Report and DirectInput cleanly
        /// concludes "no FFB" — so this is the gate that turns the Custom-VID device
        /// into a real DirectInput PID FFB device on the wire.
        ///
        /// Pool + State only. Block Load is driver-owned: HM v1.1.37+ allocates
        /// the EBI and writes BL fields synchronously inside the SetFeature(0x11)
        /// IOCTL handler, and HM's own --keep-alive probe (commit 341d014) shows
        /// that publishing only Pool + State is sufficient for FfbTest to succeed.
        /// A pre-publish via the legacy PublishPidBlockLoad override appears to
        /// leave the device in a state that crashes pid!PID_DownloadEffect on
        /// FfbTest's first CreateEffect — the same "pre-FfbTest HID activity
        /// corrupts state" pattern the HM probe hit and worked around by running
        /// FfbTest before any HidP introspection.</summary>
        public void PublishInitialState()
        {
            if (_controller == null) return;
            try
            {
                _controller.PublishPidPool(
                    ramPoolSize: RamPoolSize,
                    simultaneousEffectsMax: MaxSimultaneousEffects,
                    deviceManagedPool: true,
                    sharedParameterBlocks: true);
                _controller.PublishPidState(0, _stateFlags);
            }
            catch
            {
                // Best-effort: HM SDK throws if the controller is mid-tear-down
                // or the shared section couldn't be mapped. Either way the
                // device just won't be picked up by DirectInput as an FFB
                // device, which is the same fall-through behavior as a
                // non-FFB device.
            }
        }

        /// <summary>Decode an HM HID-output packet and update internal effect state.
        /// Caller passes the report ID and the packet payload (no leading ID byte —
        /// HMOutputPacket already separates ReportId from Data).</summary>
        /// <summary>Handle a HidD_SetFeature write from the host. Currently only
        /// the Create New Effect Feature report (ID 0x11) needs handling — game
        /// writes [effectType:1, byteCountRemaining:2] via SetFeature. As of
        /// HM v1.1.37 the driver atomically allocates the EBI and writes Block
        /// Load fields synchronously inside its SetFeature(0x11) IOCTL handler
        /// before completing the IRP, so by the time this notification arrives
        /// (8 ms-ish later via the SDK's poll loop) the BL is already canonical.
        /// We just read it back and wire the EBI into our effect-tracking
        /// dictionary.</summary>
        public void OnHidFeature(byte reportId, ReadOnlySpan<byte> data)
        {
            if (_controller == null) return;
            if (reportId != HMaestroFfbDescriptor.OutputReportId.SetEffect) return;
            if (data.Length < 1) return;

            byte effectType = data[0];
            HMPidBlockLoad bl = _controller.GetCurrentPidBlockLoad();
            if (bl.LoadStatus != PidLoadStatus.Success) return;

            lock (_lock)
            {
                var es = new EffectState { Type = MapEffectType(effectType) };
                _effects[bl.EffectBlockIndex] = es;
                _lastEbi = bl.EffectBlockIndex;
                // pid.dll writes Set Periodic / Set Constant / Set Ramp /
                // Set Condition with EBI=0 BEFORE issuing SetFeature(0x11)
                // to allocate the EBI. The pre-allocation magnitude lands
                // in _pending; bind it to the freshly created effect here.
                DrainPendingInto(es);
            }
        }

        // Pending parameter buffer for EBI=0 writes. pid.dll's flow is:
        //   1. Set Periodic / Set Constant / Set Ramp / Set Condition with EBI=0
        //   2. SetFeature(0x11) Create New Effect → driver allocates EBI=N
        //   3. Set Effect (0x11) with EBI=N to bind type/duration/direction
        // Step 1 carries the magnitude/period — we capture it here, then OnHidFeature
        // (step 2) drains it into the freshly created effect at EBI=N.
        private EffectState _pending;

        private EffectState GetOrCreateForUpdate(byte ebi)
        {
            if (ebi == 0)
            {
                if (_pending == null) _pending = new EffectState();
                return _pending;
            }
            if (!_effects.TryGetValue(ebi, out var es))
            {
                // EBI != 0 but unknown — pre-create. Some pid.dll dispatch
                // orders the type-specific write before our OnHidFeature
                // (Create) callback drains, so we may see Set X with the
                // real EBI before the effect is registered. Build it now;
                // OnHidFeature still wires _lastEbi when the SetFeature
                // notification eventually arrives.
                es = new EffectState();
                _effects[ebi] = es;
            }
            return es;
        }

        public void OnHidOutput(byte reportId, ReadOnlySpan<byte> data)
        {
            try
            {
                lock (_lock)
                {
                    switch (reportId)
                    {
                        case HMaestroFfbDescriptor.OutputReportId.SetEffect:        DecodeSetEffect(data); break;
                        case HMaestroFfbDescriptor.OutputReportId.SetCondition:     DecodeSetCondition(data); break;
                        case HMaestroFfbDescriptor.OutputReportId.SetPeriodic:      DecodeSetPeriodic(data); break;
                        case HMaestroFfbDescriptor.OutputReportId.SetConstantForce: DecodeSetConstant(data); break;
                        case HMaestroFfbDescriptor.OutputReportId.SetRampForce:     DecodeSetRamp(data); break;
                        case HMaestroFfbDescriptor.OutputReportId.EffectOperation:  DecodeEffectOperation(data); break;
                        case HMaestroFfbDescriptor.OutputReportId.BlockFree:        DecodeBlockFree(data); break;
                        case HMaestroFfbDescriptor.OutputReportId.DeviceControl:    DecodeDeviceControl(data); break;
                        case HMaestroFfbDescriptor.OutputReportId.DeviceGain:       DecodeDeviceGain(data); break;
                    }
                }
            }
            catch
            {
                // Decoder errors are recoverable: a malformed packet just
                // doesn't update effect state. The next well-formed packet
                // re-syncs.
            }
        }

        /// <summary>Aggregate running effects into the supplied Vibration.
        /// Mirrors the v2 ApplyMotorOutput polar-split + dominant-effect-passthrough.</summary>
        public void Apply(Vibration vib)
        {
            if (vib == null) return;

            double leftSum = 0, rightSum = 0;
            uint dominantType = 0;
            double dominantMag = 0;
            short dominantSignedMag = 0;
            ushort dominantDir = 0;
            uint dominantPeriod = 0;
            EffectState conditionEffect = null;

            lock (_lock)
            {
                foreach (var kv in _effects)
                {
                    var es = kv.Value;
                    if (!es.Running) continue;

                    double absMag = Math.Abs(es.Magnitude);
                    if (absMag == 0) continue;

                    double mag = absMag * (es.Gain / 255.0);

                    if (mag > dominantMag)
                    {
                        dominantMag = mag;
                        dominantType = (uint)es.Type;
                        dominantSignedMag = (short)Math.Clamp(es.Magnitude, -10000, 10000);
                        dominantDir = es.Direction;
                        dominantPeriod = es.Period;
                    }

                    bool isCondition =
                        es.Type == FfbEffectTypes.Spring  || es.Type == FfbEffectTypes.Damper ||
                        es.Type == FfbEffectTypes.Inertia || es.Type == FfbEffectTypes.Friction;
                    if (isCondition && es.ConditionAxisCount > 0)
                        conditionEffect = es;

                    // HID polar direction: where force COMES FROM. Adding 180° converts
                    // "from" to "toward" so a force coming from East (90°) → push West
                    // → bias the LEFT motor. sin(angleRad): 0 = balanced, +1 = right
                    // bias, -1 = left bias.
                    double angleDeg = ((es.Direction / 32767.0) * 360.0 + 180.0) % 360.0;
                    double angleRad = angleDeg * Math.PI / 180.0;
                    double sinVal = Math.Sin(angleRad);
                    double leftScale  = Math.Clamp(0.5 - sinVal * 0.5, 0.0, 1.0);
                    double rightScale = Math.Clamp(0.5 + sinVal * 0.5, 0.0, 1.0);

                    leftSum  += mag * leftScale;
                    rightSum += mag * rightScale;
                }

                double gainFactor = _deviceGain / 255.0;
                leftSum  *= gainFactor;
                rightSum *= gainFactor;

                ushort leftVal  = (ushort)Math.Min(65535, (int)(leftSum  * 65535.0 / 10000.0));
                ushort rightVal = (ushort)Math.Min(65535, (int)(rightSum * 65535.0 / 10000.0));

                vib.LeftMotorSpeed = leftVal;
                vib.RightMotorSpeed = rightVal;

                vib.HasDirectionalData = dominantMag > 0;
                if (vib.HasDirectionalData)
                {
                    vib.EffectType = dominantType;
                    vib.SignedMagnitude = dominantSignedMag;
                    vib.Direction = dominantDir;
                    vib.Period = dominantPeriod;
                    vib.DeviceGain = _deviceGain;
                }
                else
                {
                    vib.EffectType = 0;
                    vib.SignedMagnitude = 0;
                    vib.Direction = 0;
                    vib.Period = 0;
                }

                if (conditionEffect != null)
                {
                    vib.HasConditionData = true;
                    if (vib.ConditionAxes == null || vib.ConditionAxes.Length < conditionEffect.ConditionAxisCount)
                        vib.ConditionAxes = new ConditionAxisData[conditionEffect.ConditionAxisCount];
                    vib.ConditionAxisCount = conditionEffect.ConditionAxisCount;
                    for (int i = 0; i < conditionEffect.ConditionAxisCount; i++)
                    {
                        var src = conditionEffect.ConditionAxes[i];
                        vib.ConditionAxes[i] = new ConditionAxisData
                        {
                            PositiveCoefficient = src.PosCoeff,
                            NegativeCoefficient = src.NegCoeff,
                            Offset = src.CenterPointOffset,
                            DeadBand = (uint)src.DeadBand,
                            PositiveSaturation = src.PosSatur,
                            NegativeSaturation = src.NegSatur,
                        };
                    }
                }
                else
                {
                    vib.HasConditionData = false;
                    vib.ConditionAxisCount = 0;
                }
            }
        }

        // ── Per-report decoders ─────────────────────────────────────────────
        // Field offsets follow the byte layout that the descriptor in
        // HMaestroFfbDescriptor produces. The first byte of each report's
        // data buffer is always Effect Block Index (1-based, 1..100), then
        // report-specific fields.

        // Set Effect: [EBI][EffectType][Duration:2][TriggerRpt:2][SamplePeriod:2][StartDelay:2][Gain][TriggerButton][AxesEnable+DirEnable bits][Direction[0]:2][Direction[1]:2][TypeSpecific[0]:2][TypeSpecific[1]:2]
        private void DecodeSetEffect(ReadOnlySpan<byte> d)
        {
            if (d.Length < 18) return;
            byte ebi = d[0];
            byte effectType = d[1];
            ushort duration = ReadU16(d, 2);
            byte gain = d[10];
            // d[11] = TriggerButton, d[12] = AxesEnable+DirEnable bits
            ushort direction = ReadU16(d, 13);

            // Without the Block Load Feature report (0x12) in the descriptor,
            // dinput8 picks EBIs internally and never round-trips through
            // SetFeature(0x11), so OnHidFeature (Create New Effect) never
            // fires. Set Effect with a non-zero EBI is the first packet that
            // identifies the new effect, so drain any EBI=0 parameter writes
            // queued by pid.dll's pre-allocation pass into this effect here.
            DrainPendingInto(GetOrCreate(ebi));

            if (_effects.TryGetValue(ebi, out var es))
            {
                es.Type = MapEffectType(effectType);
                es.Gain = gain;
                es.Duration = duration;
                es.Direction = direction;
            }
        }

        // Apply any EBI=0 buffered parameters into the supplied effect.
        // Keeps Magnitude/Period/condition data when the source field is set;
        // doesn't clobber an existing magnitude with zero.
        private void DrainPendingInto(EffectState es)
        {
            if (_pending == null || es == null) return;
            if (_pending.Magnitude != 0) es.Magnitude = _pending.Magnitude;
            if (_pending.Period != 0) es.Period = _pending.Period;
            if (_pending.ConditionAxisCount > 0)
            {
                for (int i = 0; i < _pending.ConditionAxisCount && i < es.ConditionAxes.Length; i++)
                    es.ConditionAxes[i] = _pending.ConditionAxes[i];
                es.ConditionAxisCount = Math.Max(es.ConditionAxisCount, _pending.ConditionAxisCount);
            }
            _pending = null;
        }

        // Set Periodic: [EBI][Magnitude:2 unsigned 0-10000][Offset:2 signed][Phase:2][Period:4 ms]
        private void DecodeSetPeriodic(ReadOnlySpan<byte> d)
        {
            if (d.Length < 11) return;
            byte ebi = d[0];
            ushort magnitude = ReadU16(d, 1);
            uint period = ReadU32(d, 7);

            var es = GetOrCreateForUpdate(ebi);
            es.Magnitude = magnitude;
            es.Period = period;
        }

        // Set Constant Force: [EBI][Magnitude:2 signed -10000..+10000]
        private void DecodeSetConstant(ReadOnlySpan<byte> d)
        {
            if (d.Length < 3) return;
            byte ebi = d[0];
            short magnitude = ReadI16(d, 1);

            var es = GetOrCreateForUpdate(ebi);
            es.Magnitude = magnitude;
        }

        // Set Ramp Force: [EBI][Start:2 signed][End:2 signed]
        private void DecodeSetRamp(ReadOnlySpan<byte> d)
        {
            if (d.Length < 5) return;
            byte ebi = d[0];
            short start = ReadI16(d, 1);
            short end = ReadI16(d, 3);

            var es = GetOrCreateForUpdate(ebi);
            es.Magnitude = Math.Max(Math.Abs(start), Math.Abs(end));
        }

        // Set Condition Report (0x13). Bit layout pinned to HIDMaestro
        // v1.1.x's PID FFB block (sdk/HIDMaestro.Core/HidDescriptorBuilder.cs,
        // BuildMinimumViablePidFfbBlock — "Set Condition (Output, ID 0x13)"):
        //
        //   bits   width  field
        //   ─────  ─────  ─────────────────────────────────────────────
        //      0      8   EBI (Effect Block Index, 1..40)
        //      8      4   PBO (Parameter Block Offset, 0..3)
        //     12      2   TS[0] (Type Specific Block Offset, axis 0)
        //     14      2   TS[1] (Type Specific Block Offset, axis 1)
        //     16     16   CP Offset (signed, -10000..10000)
        //     32     16   PosCoeff (signed)
        //     48     16   NegCoeff (signed)
        //     64     16   PosSatur (unsigned, 0..10000)
        //     80     16   NegSatur (unsigned)
        //     96     16   DeadBand (unsigned)
        //    112  total = 14 bytes.
        //
        // Per-axis routing reads TS[0] and treats it as an axis index
        // (0 or 1). PBO is the spec-canonical "which parameter block"
        // selector; if condition effects produce wrong values after a
        // future HIDMaestro bump, diff this descriptor block against
        // the new HM version and decide whether to switch axisIdx to PBO.
        private void DecodeSetCondition(ReadOnlySpan<byte> d)
        {
            if (d.Length < 14) return;

            int bit = 0;
            byte   ebi      = (byte)ReadBitsU(d, ref bit, 8);
            _                = ReadBitsU(d, ref bit, 4); // PBO — see header comment
            int    ts0      = (int)ReadBitsU(d, ref bit, 2);
            _                = ReadBitsU(d, ref bit, 2); // TS[1]
            short  cpOffset = (short)ReadBitsS(d, ref bit, 16);
            short  posCoeff = (short)ReadBitsS(d, ref bit, 16);
            short  negCoeff = (short)ReadBitsS(d, ref bit, 16);
            ushort posSatur = (ushort)ReadBitsU(d, ref bit, 16);
            ushort negSatur = (ushort)ReadBitsU(d, ref bit, 16);
            ushort deadBand = (ushort)ReadBitsU(d, ref bit, 16);

            int axisIdx = ts0;
            if (axisIdx > 1) axisIdx = 0;

            var es = GetOrCreateForUpdate(ebi);
            es.ConditionAxes[axisIdx] = new ConditionAxis
            {
                CenterPointOffset = cpOffset,
                PosCoeff = posCoeff,
                NegCoeff = negCoeff,
                PosSatur = posSatur,
                NegSatur = negSatur,
                DeadBand = deadBand,
                IsY = axisIdx == 1,
            };
            if (axisIdx + 1 > es.ConditionAxisCount)
                es.ConditionAxisCount = axisIdx + 1;
            es.Magnitude = Math.Max(Math.Abs(posCoeff), Math.Abs(negCoeff));
        }

        // Effect Operation: [EBI][Operation 1=Start, 2=StartSolo, 3=Stop][LoopCount]
        private void DecodeEffectOperation(ReadOnlySpan<byte> d)
        {
            if (d.Length < 2) return;
            byte ebi = d[0];
            byte op = d[1];

            if (op == 1 || op == 2) // EFF_START or EFF_SOLO
            {
                if (op == 2)
                    foreach (var kv in _effects)
                        if (kv.Key != ebi) kv.Value.Running = false;
                if (_effects.TryGetValue(ebi, out var es))
                {
                    // pid.dll's SetParameters update path writes Set
                    // Constant / Set Periodic / Set Ramp / Set Condition
                    // with EBI=0 immediately before Effect Operation Start
                    // EBI=N. Bind the pending magnitude/period to the
                    // about-to-start effect.
                    DrainPendingInto(es);
                    es.Running = true;
                    _lastEbi = ebi;
                    _stateFlags |= PidStateFlags.EffectPlaying;
                    _controller?.PublishPidState(_lastEbi, _stateFlags);
                }
            }
            else if (op == 3) // EFF_STOP
            {
                if (_effects.TryGetValue(ebi, out var es))
                {
                    es.Running = false;
                    if (!AnyEffectRunningLocked()) _stateFlags &= ~PidStateFlags.EffectPlaying;
                    _controller?.PublishPidState(_lastEbi, _stateFlags);
                }
            }
        }

        // Block Free: [EBI]. Driver clears its allocator bitmap synchronously
        // inside the SetOutput(0x1F) IOCTL — we just drop our effect-state entry.
        private void DecodeBlockFree(ReadOnlySpan<byte> d)
        {
            if (d.Length < 1) return;
            byte ebi = d[0];
            _effects.Remove(ebi);
            if (!AnyEffectRunningLocked()) _stateFlags &= ~PidStateFlags.EffectPlaying;
            _controller?.PublishPidState(_lastEbi, _stateFlags);
        }

        // Device Control: [Op 1=Enable Actuators, 2=Disable, 3=StopAll, 4=DeviceReset, 5=DevicePause, 6=DeviceContinue]
        private void DecodeDeviceControl(ReadOnlySpan<byte> d)
        {
            if (d.Length < 1) return;
            byte op = d[0];
            switch (op)
            {
                case 1: // enable actuators
                    _stateFlags |= PidStateFlags.ActuatorsEnabled;
                    break;
                case 2: // disable actuators
                    foreach (var kv in _effects) kv.Value.Running = false;
                    _stateFlags &= ~PidStateFlags.ActuatorsEnabled;
                    _stateFlags &= ~PidStateFlags.EffectPlaying;
                    break;
                case 3: // stop all
                    foreach (var kv in _effects) kv.Value.Running = false;
                    _stateFlags &= ~PidStateFlags.EffectPlaying;
                    break;
                case 4: // device reset
                    _effects.Clear();
                    _pending = null;
                    _deviceGain = 255;
                    _lastEbi = 0;
                    _stateFlags = PidStateFlags.ActuatorsEnabled | PidStateFlags.ActuatorPower;
                    break;
                case 5: // device pause
                    _stateFlags |= PidStateFlags.DeviceIsPaused;
                    break;
                case 6: // device continue
                    _stateFlags &= ~PidStateFlags.DeviceIsPaused;
                    break;
            }
            _controller?.PublishPidState(_lastEbi, _stateFlags);
        }

        // Device Gain: [Gain:1 byte 0-255]
        private void DecodeDeviceGain(ReadOnlySpan<byte> d)
        {
            if (d.Length < 1) return;
            _deviceGain = d[0];
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private EffectState GetOrCreate(byte ebi)
        {
            if (!_effects.TryGetValue(ebi, out var es))
            {
                es = new EffectState();
                _effects[ebi] = es;
            }
            return es;
        }

        private bool AnyEffectRunningLocked()
        {
            foreach (var kv in _effects) if (kv.Value.Running) return true;
            return false;
        }

        private static ushort ReadU16(ReadOnlySpan<byte> d, int offset)
            => (ushort)(d[offset] | (d[offset + 1] << 8));

        private static short ReadI16(ReadOnlySpan<byte> d, int offset)
            => (short)(d[offset] | (d[offset + 1] << 8));

        private static uint ReadU32(ReadOnlySpan<byte> d, int offset)
            => (uint)(d[offset] | (d[offset + 1] << 8) | (d[offset + 2] << 16) | (d[offset + 3] << 24));

        // HID packs fields LSB-first within a byte; multi-byte fields
        // continue into the next byte's LSB. A 16-bit field starting at
        // bit offset 16 occupies bytes 2 (low) and 3 (high), matching
        // ReadU16's little-endian behavior.
        private static uint ReadBitsU(ReadOnlySpan<byte> d, ref int bitOffset, int width)
        {
            uint v = 0;
            int outBit = 0;
            while (width > 0)
            {
                int byteIdx = bitOffset >> 3;
                if (byteIdx >= d.Length) break;
                int bitInByte = bitOffset & 7;
                int take = Math.Min(8 - bitInByte, width);
                uint chunk = (uint)((d[byteIdx] >> bitInByte) & ((1 << take) - 1));
                v |= chunk << outBit;
                outBit += take;
                bitOffset += take;
                width -= take;
            }
            return v;
        }

        private static int ReadBitsS(ReadOnlySpan<byte> d, ref int bitOffset, int width)
        {
            uint u = ReadBitsU(d, ref bitOffset, width);
            if (width > 0 && width < 32 && (u & (1u << (width - 1))) != 0)
                u |= ~((1u << width) - 1);
            return (int)u;
        }

        // HID PID Effect Type → FfbEffectTypes constants
        private static uint MapEffectType(byte hidType) => hidType switch
        {
            0x01 => FfbEffectTypes.Const,    // Constant Force
            0x02 => FfbEffectTypes.Ramp,     // Ramp
            0x03 => FfbEffectTypes.Square,
            0x04 => FfbEffectTypes.Sine,
            0x05 => FfbEffectTypes.Triangle,
            0x06 => FfbEffectTypes.SawUp,
            0x07 => FfbEffectTypes.SawDown,
            0x08 => FfbEffectTypes.Spring,
            0x09 => FfbEffectTypes.Damper,
            0x0A => FfbEffectTypes.Inertia,
            0x0B => FfbEffectTypes.Friction,
            _ => FfbEffectTypes.None,
        };

        // ── Internal effect-state types ─────────────────────────────────────

        private sealed class EffectState
        {
            public uint Type;
            public int Magnitude;        // signed for constant (-10000..+10000), abs for others
            public byte Gain = 255;
            public ushort Duration;
            public bool Running;
            public ushort Direction;     // HID logical 0..32767 → 0..360°
            public uint Period;
            public ConditionAxis[] ConditionAxes = new ConditionAxis[2];
            public int ConditionAxisCount;
        }

        private struct ConditionAxis
        {
            public short CenterPointOffset;
            public short PosCoeff;
            public short NegCoeff;
            public ushort PosSatur;
            public ushort NegSatur;
            public ushort DeadBand;
            public bool IsY;
        }
    }
}
