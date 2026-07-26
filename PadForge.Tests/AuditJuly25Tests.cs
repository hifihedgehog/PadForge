using System;
using System.Collections.Generic;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guards for the 2026-07-25 code-audit fixes. Each class pins one
    /// finding; deleting that finding's fix turns the matching tests red.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PerVcEvaluatorSuppressionTests : IDisposable
    {
        private static readonly Guid DevGuid = new("2b7ce150-6a51-45f2-9f76-31337a0d5e01");
        private static readonly string DevGuidStr = DevGuid.ToString();
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private InputManager _im;
        private CustomInputState _state;

        public PerVcEvaluatorSuppressionTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            // Release the held button and rebuild so the STATIC per-slot
            // suppression set drains before the next test class runs.
            if (_state != null && _im != null)
            {
                _state.Buttons[0] = false;
                _im.RebuildConsumedTriggerSources();
            }
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        /// <summary>One online device on slot 0 plus a consume-armed macro
        /// triggered by its raw Button 0 (the ConsumeRawTriggerTests
        /// arrangement), held so the suppression set is populated.</summary>
        private void ArmConsume(bool consume = true)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            _state = new CustomInputState();
            var ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "Audit Pad",
                IsOnline = true,
                InputState = _state,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(
                    new UserSetting { InstanceGuid = DevGuid, MapTo = 0 });
            _im = new InputManager();
            _im.MacroSnapshots[0] = new[]
            {
                new MacroItem
                {
                    Name = "consume",
                    IsEnabled = true,
                    PadIndex = 0,
                    TriggerMode = MacroTriggerMode.OnPress,
                    RepeatMode = MacroRepeatMode.Once,
                    ConsumeTriggerButtons = consume,
                    TriggerInputs = "in:" + DevGuidStr + ":btn:0",
                },
            };
            _state.Buttons[0] = true;
            _im.RebuildConsumedTriggerSources();
        }

        private static MappingSet SingleSourceSet(string target)
        {
            var set = new MappingSet();
            var row = new MappingRow { Target = target };
            row.Sources.Add(new MappingSource { Descriptor = "Button 0", DeviceGuid = DevGuidStr });
            set.Rows.Add(row);
            return set;
        }

        [Fact]
        public void Button_SingleSource_ReadsReleasedWhileConsumed()
        {
            ArmConsume();
            var set = SingleSourceSet("ExtButton1");

            // The row still owns the target (no legacy fallback), and the
            // consumed press reads released.
            Assert.True(InputManager.TryEvaluateMappingSetButton(
                _state, set, DevGuidStr, 0, "ExtButton1", 50, out bool value));
            Assert.False(value);

            // Same-window positive control: consume off, same held button.
            ArmConsume(consume: false);
            Assert.True(InputManager.TryEvaluateMappingSetButton(
                _state, set, DevGuidStr, 0, "ExtButton1", 50, out value));
            Assert.True(value);
        }

        [Fact]
        public void BipolarAxis_SingleSource_ReadsCenteredWhileConsumed()
        {
            ArmConsume(consume: false);
            var set = SingleSourceSet("ExtAxis1");
            Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(
                _state, set, DevGuidStr, 0, "ExtAxis1", out short live));
            Assert.NotEqual(0, live); // positive control: the press reads

            ArmConsume();
            Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(
                _state, set, DevGuidStr, 0, "ExtAxis1", out short value));
            Assert.Equal(0, value);
        }

        [Fact]
        public void RawTrigger_SingleSource_ReadsReleasedWhileConsumed()
        {
            ArmConsume(consume: false);
            var set = SingleSourceSet("ExtTrigger1");
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(
                _state, set, DevGuidStr, 0, "ExtTrigger1", out short live));
            Assert.NotEqual(short.MinValue, live); // positive control

            ArmConsume();
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(
                _state, set, DevGuidStr, 0, "ExtTrigger1", out short value));
            Assert.Equal(short.MinValue, value); // released = 0%
        }

        [Fact]
        public void TouchpadAxis_SuppressedSourceReadsInactive()
        {
            ArmConsume(consume: false);
            var set = SingleSourceSet("TouchpadX1");
            Assert.True(InputManager.TryEvaluateMappingSetTouchpadAxis(
                _state, set, DevGuidStr, 0, "TouchpadX1", 0, out short live));
            Assert.NotEqual(0, live); // positive control: non-touchpad source is active

            // Consumed: the source reads inactive, activeCount drops to 0,
            // and the evaluator reports hold-last-position.
            ArmConsume();
            Assert.False(InputManager.TryEvaluateMappingSetTouchpadAxis(
                _state, set, DevGuidStr, 0, "TouchpadX1", 0, out _));
        }

        [Fact]
        public void Lookup_FallsBackToEmptyKey_ForPinnedRowOnAnotherDevice()
        {
            // The consume set carries the concrete key plus the empty-guid
            // twin. A row PINNED to a different device guid must still be
            // suppressed via the empty key (consume-means-consume).
            ArmConsume();
            Assert.True(InputManager.IsSourceSuppressedPostpone(
                0, "99999999-9999-9999-9999-999999999999", "Button 0"));
        }

        [Fact]
        public void AddPostponeKey_AddsTheEmptyGuidTwin()
        {
            var set = new HashSet<(string Guid, string Desc)>();
            InputManager.AddPostponeKey(set, DevGuidStr, "Button 0");
            Assert.Contains((DevGuidStr, "Button 0"), set);
            Assert.Contains(("", "Button 0"), set);

            // An any-device activator authors the empty key once, not twice.
            var set2 = new HashSet<(string Guid, string Desc)>();
            InputManager.AddPostponeKey(set2, "", "Button 0");
            Assert.Single(set2);
        }
    }

    /// <summary>Finding F: the v3 caps-tail catch must reset EVERY
    /// extension-carried field on EVERY record, not just the NFC bit.</summary>
    public class LinkV3CatchResetTests
    {
        [Fact]
        public void TruncatedV3Tail_ResetsBothCapabilityFlags_OnEveryRecord()
        {
            var a = new RemotePeerDeviceInfo
            {
                PeerLocalDeviceId = "a", Name = "A",
                InputDeviceType = InputDeviceType.Gamepad,
                HasNfcReader = true, HasGyroAux = true,
            };
            var b = new RemotePeerDeviceInfo
            {
                PeerLocalDeviceId = "b", Name = "B",
                InputDeviceType = InputDeviceType.Gamepad,
                HasNfcReader = true, HasGyroAux = true,
            };
            var full = LinkConnection.EncodeDeviceList(new[] { a, b });

            // Drop the final byte: the v3 tail is the last section, so the
            // magic survives, record A's caps byte parses (flags applied),
            // and record B's read throws mid-loop into the catch.
            var truncated = new byte[full.Length - 1];
            Array.Copy(full, truncated, truncated.Length);

            var list = LinkConnection.DecodeDeviceList(truncated);
            Assert.Equal(2, list.Count);
            Assert.All(list, d =>
            {
                Assert.False(d.HasNfcReader);
                Assert.False(d.HasGyroAux);
            });
        }
    }

    /// <summary>Findings N/O: MIDI channel-mode conformance.</summary>
    public class MidiChannelModeTests
    {
        private static MidiInputDevice Dev() => new("audit-test-endpoint", "Audit MIDI");

        [Fact]
        public void Cc121_ResetsRp015Lanes_AndNothingElse()
        {
            var dev = Dev();
            dev.SetNote(60, true);
            dev.SetPitchBend(0);          // hard-left bend
            dev.SetCc(1, 100);            // mod wheel
            dev.SetCc(11, 3);             // expression
            dev.SetCc(64, 127);           // sustain pedal
            dev.SetCc(99, 5);             // NRPN MSB
            dev.SetCc(7, 90);             // volume: must survive

            dev.SetCc(121, 0);
            var s = dev.GetCurrentState();

            Assert.Equal(32768, s.Midi.PitchBend);
            Assert.Equal((byte)0, s.Midi.Cc[1]);
            Assert.Equal((byte)127, s.Midi.Cc[11]);
            Assert.Equal((byte)0, s.Midi.Cc[64]);
            Assert.Equal((byte)127, s.Midi.Cc[99]);
            Assert.Equal((byte)90, s.Midi.Cc[7]);
            // Reset All Controllers releases NO notes (that's 123's job).
            Assert.True(s.Midi.Notes[60]);
        }

        [Theory]
        [InlineData(124)]
        [InlineData(125)]
        [InlineData(126)]
        [InlineData(127)]
        public void Cc124Through127_ImplyAllNotesOff(int cc)
        {
            var dev = Dev();
            dev.SetNote(60, true);
            dev.SetCc(cc, 0);
            Assert.False(dev.GetCurrentState().Midi.Notes[60]);
        }

        [Fact]
        public void Cc122_LocalControl_ReleasesNothing()
        {
            var dev = Dev();
            dev.SetNote(60, true);
            dev.SetCc(122, 0);
            Assert.True(dev.GetCurrentState().Midi.Notes[60]);
        }

        [Fact]
        public void Cc121_CancelsQueuedEncoderPulses_OnResetLanes()
        {
            // Positive control: a +1 detent on CC 1 pulses CcUp[1].
            var live = Dev();
            live.SetCc(1, 0x41);
            Assert.True(live.GetCurrentState().Midi.CcUp[1]);

            // Reset before the poll reads it: the pulse must be gone.
            var reset = Dev();
            reset.SetCc(1, 0x41);
            reset.SetCc(121, 0);
            Assert.False(reset.GetCurrentState().Midi.CcUp[1]);
        }
    }

    /// <summary>Findings P/Q: PID Device Control semantics. Pause freezes
    /// and mutes. Actuators-off mutes without destroying effect state.</summary>
    public class PidDeviceControlTests
    {
        private static HMaestroFfbDecoder StartedConstant(ushort duration = 0xFFFF)
        {
            var dec = new HMaestroFfbDecoder(null);
            dec.OnHidOutput(0x11, new byte[] { 1, 0x01, (byte)(duration & 0xFF), (byte)(duration >> 8) });
            dec.OnHidOutput(0x15, new byte[] { 1, 0x10, 0x27 }); // +10000 constant
            dec.OnHidOutput(0x1A, new byte[] { 1, 1, 1 });       // EFF_START
            return dec;
        }

        [Fact]
        public void DevicePause_MutesEverything_ContinueResumes()
        {
            var dec = StartedConstant();
            var vib = new Vibration();
            dec.Apply(vib);
            Assert.NotEqual((ushort)0, vib.LeftMotorSpeed);
            Assert.True(vib.HasDirectionalData);

            dec.OnHidOutput(0x1C, new byte[] { 5 }); // DC_DEVICE_PAUSE
            var paused = new Vibration();
            dec.Apply(paused);
            Assert.Equal((ushort)0, paused.LeftMotorSpeed);
            Assert.Equal((ushort)0, paused.RightMotorSpeed);
            Assert.False(paused.HasDirectionalData);
            Assert.False(paused.HasConditionData);
            Assert.Equal(((ushort)0, (ushort)0), dec.LastComputedMotors);

            dec.OnHidOutput(0x1C, new byte[] { 6 }); // DC_DEVICE_CONTINUE
            var resumed = new Vibration();
            dec.Apply(resumed);
            Assert.NotEqual((ushort)0, resumed.LeftMotorSpeed);
        }

        [Fact]
        public void DevicePause_FreezesFiniteEffectClocks()
        {
            // Round nine: the life was 100 ms with the pause arriving on
            // the very next line, so under heavy machine load (a
            // concurrent build, a second test host) the scheduler could
            // starve that gap past 100 ms, the effect expired BEFORE the
            // pause landed, and the resume found nothing to keep alive.
            // The product was right every time; the margin was too thin.
            // A one-second life needs a full second of starvation to
            // break, which is far outside anything observed.
            var dec = StartedConstant(duration: 1000);
            dec.Apply(new Vibration());

            dec.OnHidOutput(0x1C, new byte[] { 5 });
            System.Threading.Thread.Sleep(1200); // far past the 1000 ms life
            dec.OnHidOutput(0x1C, new byte[] { 6 });

            // The paused span must not count against the duration.
            var vib = new Vibration();
            dec.Apply(vib);
            Assert.NotEqual((ushort)0, vib.LeftMotorSpeed);
        }

        [Fact]
        public void DisableActuators_Mutes_EnableRestoresTheSameEffects()
        {
            var dec = StartedConstant();
            dec.Apply(new Vibration());

            dec.OnHidOutput(0x1C, new byte[] { 2 }); // DC_DISABLE_ACTUATORS
            var muted = new Vibration();
            dec.Apply(muted);
            Assert.Equal((ushort)0, muted.LeftMotorSpeed);
            Assert.False(muted.HasDirectionalData);
            Assert.Equal(((ushort)0, (ushort)0), dec.LastComputedMotors);

            // Enable restores the SAME playing effects: mute, not stop.
            dec.OnHidOutput(0x1C, new byte[] { 1 }); // DC_ENABLE_ACTUATORS
            var restored = new Vibration();
            dec.Apply(restored);
            Assert.NotEqual((ushort)0, restored.LeftMotorSpeed);
        }

        [Fact]
        public void DisableActuators_StillExpiresFiniteEffects()
        {
            var dec = StartedConstant(duration: 40);
            dec.OnHidOutput(0x1C, new byte[] { 2 });
            System.Threading.Thread.Sleep(100);
            dec.OnHidOutput(0x1C, new byte[] { 1 });

            // Unlike pause, disabled effects keep playing internally and
            // expire on schedule.
            var vib = new Vibration();
            dec.Apply(vib);
            Assert.Equal((ushort)0, vib.LeftMotorSpeed);
        }
    }

    /// <summary>Findings B/C: the Sony motor trust gate, pinned as a truth
    /// table on the extracted predicate. (The OutputDecoded wiring itself
    /// needs a live HMController and is verified by code read; these lock
    /// the gate semantics the wiring calls.)</summary>
    public class SonyMotorGateTests
    {
        [Fact]
        public void FullyValidReport_Passes()
        {
            Assert.True(HMaestroVirtualController.SonyMotorsValid(
                48, 48, true, (byte)0x01, 0x01));
            // DS5 HAPTICS_SELECT alone satisfies the 0x03 mask.
            Assert.True(HMaestroVirtualController.SonyMotorsValid(
                78, 78, true, (byte)0x02, 0x03));
        }

        [Fact]
        public void LightbarOnlyReport_IsIgnoreNotStop()
        {
            // Motor flag clear (lightbar-only flags set): motors invalid.
            Assert.False(HMaestroVirtualController.SonyMotorsValid(
                48, 48, true, (byte)0x04, 0x01));
            // ...and only a Sony profile blocks the write on that verdict.
            Assert.False(HMaestroVirtualController.MotorWriteAllowed(0x054C, false));
            Assert.True(HMaestroVirtualController.MotorWriteAllowed(0x057E, false));
            Assert.True(HMaestroVirtualController.MotorWriteAllowed(0x054C, true));
        }

        [Fact]
        public void TruncatedCorruptOrFlaglessReports_Fail()
        {
            Assert.False(HMaestroVirtualController.SonyMotorsValid(
                47, 48, true, (byte)0x01, 0x01));  // short (CrcValid lies on truncation)
            Assert.False(HMaestroVirtualController.SonyMotorsValid(
                48, 48, false, (byte)0x01, 0x01)); // corrupt CRC
            Assert.False(HMaestroVirtualController.SonyMotorsValid(
                48, 48, true, null, 0x01));        // flag field absent
            Assert.False(HMaestroVirtualController.SonyMotorsValid(
                48, 48, true, 1, 0x01));           // wrong field type (boxed int)
        }
    }
}
