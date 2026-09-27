using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Ring-Con sources (hifihedgehog/SDL#33 Part 13). The fork posts the
    /// ring's raw strain on a right Joy-Con's joystick axis 7 and its first
    /// nonzero reading after polling starts as the rest property.
    /// SdlDeviceWrapper.NormalizeRingConStrain turns the two into
    /// CustomInputState.RingConStrain, and "Ring-Con Squeeze" and "Ring-Con
    /// Pull" each read one direction of it.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class RingConSourceTests
    {
        private const string Squeeze = SourceCoercion.RingConSqueezeDescriptor;
        private const string Pull = SourceCoercion.RingConPullDescriptor;

        private static MappingSource Src(string descriptor, int deadZone = 0, bool invert = false) => new()
        {
            Descriptor = descriptor,
            DeadZone = deadZone,
            Invert = invert,
        };

        private static CustomInputState State(float strain) => new() { RingConStrain = strain };

        // ── The wrapper's normalization ──

        /// <summary>A rest of 0x0A80 puts the high byte at 0x0A, Ringcon-Driver's
        /// resting value. Eight high-byte steps are full scale.</summary>
        [Theory]
        [InlineData(0x0A80, 0f)]
        [InlineData(0x0E80, 0.5f)]    // four steps: the WebHID demo's 0x0400 press radius
        [InlineData(0x1280, 1f)]      // eight steps: a full squeeze
        [InlineData(0x1880, 1f)]      // past full scale: clamped
        [InlineData(0x0680, -0.5f)]
        [InlineData(0x0280, -1f)]
        [InlineData(0xFF80, -1f)]     // a hard pull past zero stays a reading, clamped
        public void Strain_ScalesFromTheRest(int raw, float expected)
        {
            float v = SdlDeviceWrapper.NormalizeRingConStrain(unchecked((short)raw), 0x0A80);
            Assert.Equal(expected, v, precision: 5);
        }

        /// <summary>osc-ringcon reports no Ring-Con on a zero high byte and
        /// sends its idle value, so a ring leaving the rail reads 0 here
        /// rather than a full pull or a held squeeze.</summary>
        [Theory]
        [InlineData(0x0012)]
        [InlineData(0x00FF)]
        [InlineData(0x0000)]
        public void AZeroHighByte_ReadsNoRingCon(int raw)
        {
            Assert.Equal(0f, SdlDeviceWrapper.NormalizeRingConStrain(unchecked((short)raw), 0x0A80));
        }

        [Fact]
        public void NoRestYet_ReadsZero()
        {
            Assert.Equal(0f, SdlDeviceWrapper.NormalizeRingConStrain(0x1280, 0));
            // A rest whose high byte is 0 is no reading either.
            Assert.Equal(0f, SdlDeviceWrapper.NormalizeRingConStrain(0x1280, 0x0040));
            // A rest past zero is a reading.
            Assert.Equal(0.5f, SdlDeviceWrapper.NormalizeRingConStrain(0x0380, -128), precision: 5);
        }

        // ── The two sources ──

        [Fact]
        public void Descriptors_ClassifyAsTheRingConFamily()
        {
            Assert.Equal(SourceCoercion.SourceType.RingCon, SourceCoercion.ClassifyDescriptor(Squeeze));
            Assert.Equal(SourceCoercion.SourceType.RingCon, SourceCoercion.ClassifyDescriptor(Pull));
            Assert.True(SourceCoercion.IsRingConDescriptor(" Ring-Con Pull "));
            Assert.False(SourceCoercion.IsRingConDescriptor("Ring-Con"));
            Assert.False(SourceCoercion.IsRingConDescriptor("ring-con squeeze"));
            // A button row shows the threshold slider these reads use.
            Assert.True(SourceCoercion.IsThresholdedButtonFamily(Squeeze));
            Assert.True(SourceCoercion.IsThresholdedButtonFamily(Pull));
        }

        [Theory]
        [InlineData(0.60f, true, false)]
        [InlineData(0.40f, false, false)]
        [InlineData(-0.60f, false, true)]
        [InlineData(-0.40f, false, false)]
        [InlineData(0f, false, false)]
        public void AsButtons_EachDirectionFiresPastTheThreshold(float strain, bool squeezed, bool pulled)
        {
            Assert.Equal(squeezed, SourceCoercion.EvaluateForButtonTarget(State(strain), Src(Squeeze), 50));
            Assert.Equal(pulled, SourceCoercion.EvaluateForButtonTarget(State(strain), Src(Pull), 50));
        }

        [Fact]
        public void PerRowDeadZone_OverridesTheGlobalThreshold()
        {
            var state = State(0.40f);
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, Src(Squeeze), 25));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, Src(Squeeze, deadZone: 60), 25));
        }

        [Fact]
        public void AsTriggers_EachDirectionPullsItsOwn()
        {
            Assert.Equal(0.37f, SourceCoercion.EvaluateForTriggerTarget(State(0.37f), Src(Squeeze)), precision: 5);
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(State(0.37f), Src(Pull)));
            Assert.Equal(0.8f, SourceCoercion.EvaluateForTriggerTarget(State(-0.8f), Src(Pull)), precision: 5);
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(State(-0.8f), Src(Squeeze)));
            // Invert on a trigger reads 1 - v, as IR Brightness does.
            Assert.Equal(0.75f, SourceCoercion.EvaluateForTriggerTarget(State(0.25f), Src(Squeeze, invert: true)), precision: 5);
        }

        /// <summary>One stick axis from both directions: squeeze pushes it up,
        /// and the pull source with Invert pushes it down.</summary>
        [Fact]
        public void OnAStickAxis_InvertTurnsThePullNegative()
        {
            Assert.Equal(0.8f, SourceCoercion.EvaluateForBipolarAxisTarget(State(0.8f), Src(Squeeze)), precision: 5);
            Assert.Equal(-0.8f, SourceCoercion.EvaluateForBipolarAxisTarget(State(-0.8f), Src(Pull, invert: true)), precision: 5);
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(State(-0.8f), Src(Squeeze)));
        }

        /// <summary>A relayed frame carries whatever the peer sent, so the read
        /// clamps on its own.</summary>
        [Fact]
        public void AnOutOfRangeRelayedValue_Clamps()
        {
            Assert.Equal(1f, SourceCoercion.EvaluateForTriggerTarget(State(3.5f), Src(Squeeze)));
            Assert.Equal(1f, SourceCoercion.EvaluateForTriggerTarget(State(-3.5f), Src(Pull)));
        }

        [Fact]
        public void TheParamAndGateRead_UsesTheHalfwayPoint()
        {
            Assert.True(SourceCoercion.ReadHardwareBoolDescriptor(State(0.6f), Squeeze));
            Assert.False(SourceCoercion.ReadHardwareBoolDescriptor(State(0.4f), Squeeze));
            Assert.True(SourceCoercion.ReadHardwareBoolDescriptor(State(-0.6f), Pull));
            Assert.False(SourceCoercion.ReadHardwareBoolDescriptor(State(0.6f), Pull));
        }

        /// <summary>Every read stamps the demand latch InputService arms the
        /// Ring-Con hint from, and an engine stop clears it.</summary>
        [Fact]
        public void EveryRead_StampsTheDemandLatch()
        {
            SourceCoercion.ResetMcuDemandLatches();
            Assert.Equal(0, SourceCoercion.LastRingConReadRequestTick);
            long before = Environment.TickCount64;
            SourceCoercion.EvaluateForTriggerTarget(State(0f), Src(Pull));
            Assert.True(SourceCoercion.LastRingConReadRequestTick >= before);
            // The camera's latch is its own.
            Assert.Equal(0, SourceCoercion.LastJoyConIrReadRequestTick);
            SourceCoercion.ResetMcuDemandLatches();
            Assert.Equal(0, SourceCoercion.LastRingConReadRequestTick);
        }

        [Fact]
        public void Clone_CopiesTheStrain()
        {
            Assert.Equal(-0.42f, State(-0.42f).Clone().RingConStrain, precision: 5);
        }

        // ── Where the sources are offered ──

        [Fact]
        public void HasRingCon_IsTheRightJoyConIdentity()
        {
            Assert.True(new UserDevice { VendorId = 0x057E, ProductName = "Nintendo Switch Joy-Con (R)" }.HasRingCon);
            Assert.True(new UserDevice { VendorId = 0x057E, ProdId = 0x2008, ProductName = "Nintendo Switch Joy-Con (L/R)" }.HasRingCon);
            Assert.False(new UserDevice { VendorId = 0x057E, ProductName = "Nintendo Switch Joy-Con (L)" }.HasRingCon);
            Assert.False(new UserDevice { VendorId = 0x057E, ProductName = "Nintendo Switch 2 Joy-Con (R)" }.HasRingCon);
            Assert.False(new UserDevice { VendorId = 0x057E, ProdId = 0x2068, ProductName = "Nintendo Switch Joy-Con (L/R)" }.HasRingCon);
        }

        [Fact]
        public void ThePicker_OffersBothDirectionsOnARightJoyConOnly()
        {
            var right = new UserDevice { VendorId = 0x057E, ProdId = 0x2007, ProductName = "Nintendo Switch Joy-Con (R)" };
            var left = new UserDevice { VendorId = 0x057E, ProdId = 0x2006, ProductName = "Nintendo Switch Joy-Con (L)" };
            var rightChoices = PadForge.Common.MappingDisplayResolver.BuildInputChoices(right).Select(c => c.Descriptor).ToList();
            var leftChoices = PadForge.Common.MappingDisplayResolver.BuildInputChoices(left).Select(c => c.Descriptor).ToList();
            Assert.Contains(Squeeze, rightChoices);
            Assert.Contains(Pull, rightChoices);
            Assert.DoesNotContain(Squeeze, leftChoices);
            Assert.DoesNotContain(Pull, leftChoices);
        }

        // ── Idle disconnect ──

        /// <summary>A player exercising with the ring presses no button for
        /// minutes, so a squeeze or pull keeps the Joy-Con connected. The flex
        /// is measured from the ring's rest, so a released ring reads idle.</summary>
        [Fact]
        public void AFlexedRing_IsNotIdle()
        {
            Assert.False(IdleInputDetector.IsGamepadIdle(Neutral(0.6f)));
            Assert.False(IdleInputDetector.IsGamepadIdle(Neutral(-0.6f)));
            Assert.True(IdleInputDetector.IsGamepadIdle(Neutral(0.1f)));
            Assert.False(IdleInputDetector.IsUnchanged(Neutral(0.6f), Neutral(0.6f)));
            Assert.True(IdleInputDetector.IsUnchanged(Neutral(0.1f), Neutral(0.1f)));
        }

        private static CustomInputState Neutral(float strain)
        {
            var s = new CustomInputState { RingConStrain = strain };
            s.Axis[0] = s.Axis[1] = s.Axis[3] = s.Axis[4] = 32767;
            return s;
        }

        // ── Remote Link ──

        private static readonly CustomInputStateCodec.Caps NoSensors = new(gyro: false, accel: false);
        private static readonly CustomInputStateCodec.Caps AuxGyro = new(false, false, false, gyroAux: true);

        [Fact]
        public void TheStrain_SurvivesTheWire()
        {
            foreach (float v in new[] { 0.75f, -0.3f })
            {
                var back = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(State(v), NoSensors));
                Assert.Equal(v, back.RingConStrain);
            }
        }

        /// <summary>0 is rest or no ring, the neutral an omitted block decodes
        /// to, so a resting ring costs no bytes.</summary>
        [Fact]
        public void ARestingRing_AddsNoBytes()
        {
            Assert.Equal(CustomInputStateCodec.Encode(new CustomInputState(), NoSensors).Length,
                CustomInputStateCodec.Encode(State(0f), NoSensors).Length);
            Assert.Equal(CustomInputStateCodec.Encode(new CustomInputState(), NoSensors).Length + 7,
                CustomInputStateCodec.Encode(State(0.5f), NoSensors).Length);
        }

        /// <summary>The block rides the extension tail after GyroAux, so a
        /// decoder that knows only GyroAux reads its three floats and leaves
        /// the flex as the frame's last four bytes, which its trailing-bytes
        /// rule tolerates.</summary>
        [Fact]
        public void BehindGyroAux_TheFlexIsTheLastFourBytes()
        {
            var s = State(-0.625f);
            s.GyroAux[0] = 1.5f; s.GyroAux[1] = -2f; s.GyroAux[2] = 0.25f;
            byte[] frame = CustomInputStateCodec.Encode(s, AuxGyro);

            Assert.Equal(-0.625f, BitConverter.ToSingle(frame, frame.Length - 4));
            Assert.Equal(0.25f, BitConverter.ToSingle(frame, frame.Length - 8));

            var back = CustomInputStateCodec.Decode(frame);
            Assert.Equal(-0.625f, back.RingConStrain);
            Assert.Equal(1.5f, back.GyroAux[0]);
            Assert.Equal(0.25f, back.GyroAux[2]);
            Assert.True(frame.Length <= CustomInputStateCodec.MaxEncodedSize(s, AuxGyro));
            Assert.True(CustomInputStateCodec.Encode(State(1f), NoSensors).Length
                <= CustomInputStateCodec.MaxEncodedSize(State(1f), NoSensors));
        }

        [Fact]
        public void AFrameWithoutTheBlock_ClearsAStaleStrain()
        {
            var target = State(1f);
            Assert.True(CustomInputStateCodec.DecodeInto(CustomInputStateCodec.Encode(new CustomInputState(), NoSensors), target));
            Assert.Equal(0f, target.RingConStrain);
        }

        [Fact]
        public void ANonFiniteStrain_FailsClosed()
        {
            byte[] frame = CustomInputStateCodec.Encode(State(0.5f), NoSensors);
            BitConverter.GetBytes(float.NaN).CopyTo(frame, frame.Length - 4);
            var target = State(0.9f);
            Assert.False(CustomInputStateCodec.DecodeInto(frame, target));
            Assert.Equal(0f, target.RingConStrain);

            // Cut inside the float: the slice throws into the fail-closed catch.
            byte[] cut = CustomInputStateCodec.Encode(State(0.5f), NoSensors)[..^2];
            Assert.False(CustomInputStateCodec.DecodeInto(cut, target));
            Assert.Equal(0f, target.RingConStrain);
        }

        /// <summary>A consumer's live mapping reaches the owner of a relayed
        /// right Joy-Con as demand kind 2, at most once a second, on its own
        /// clock apart from the NFC demand's.</summary>
        [Fact]
        public void TheDemand_ShipsAsItsOwnKind()
        {
            var demand = RemoteLinkOutputRouter.SendScopedDemand;
            string id = Guid.NewGuid().ToString("N");
            var info = LinkLifetimeFixtures.Info(id, 12);
            info.PeerFingerprintHex = "owner";
            var peer = new RemotePeerDevice(info);
            var connection = LinkLifetimeFixtures.Lifetime(Array.Empty<RemotePeerDeviceInfo>());
            Assert.True(connection.AcceptPeerInventory(new[] { info }, 0, true, out _));
            var sent = new List<byte[]>();
            RemoteLinkOutputRouter.SendScopedDemand = (c, slot, deviceId, payload) =>
            {
                bool accepted = c.Send(LinkMessageType.SourceDemand, slot, payload, deviceId: deviceId);
                if (accepted) sent.Add(payload);
                return accepted;
            };
            string path = peer.DevicePath;
            try
            {
                RemoteLinkOutputRouter.Register(path, "owner", 12, connection, peer);
                RemoteLinkOutputRouter.ShipNfcDemand(path);
                RemoteLinkOutputRouter.ShipRingConDemand(path);
                RemoteLinkOutputRouter.ShipRingConDemand(path); // inside the second: dropped
                Assert.Equal(2, sent.Count);
                Assert.Equal(new[] { RemoteLinkOutputRouter.DemandKindNfc }, sent[0]);
                Assert.Equal(new[] { RemoteLinkOutputRouter.DemandKindRingCon }, sent[1]);
                Assert.Equal((byte)2, RemoteLinkOutputRouter.DemandKindRingCon);
            }
            finally
            {
                RemoteLinkOutputRouter.Unregister(path);
                RemoteLinkOutputRouter.SendScopedDemand = demand;
            }
        }
    }
}
