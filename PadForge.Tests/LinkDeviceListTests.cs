using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    /// <summary>
    /// Wire pins for the device-list framing (#138), including the metadata
    /// extension that forwards the owner's named inputs so a remote device's
    /// mapping picker and Devices-page preview read identically to local.
    /// </summary>
    public class LinkDeviceListTests
    {
        private static RemotePeerDeviceInfo ConsumerInfo() => new()
        {
            Slot = 3,
            PeerLocalDeviceId = "cc-aggregate",
            Name = "All Consumer Controls (Merged)",
            VendorId = 0x4E46,
            ProductId = 0x4350,
            SerialNumber = "serial-42",
            NumButtons = 4,
            InputDeviceType = InputDeviceType.ConsumerControl,
            HasTouchpad = true, // artificial: exercises the touchpad metadata lane in one fixture
            NumTouchpads = 2,
            TouchpadFingerCounts = new[] { 5, 2 },
            DeviceObjects = new[]
            {
                new DeviceObjectItem { Name = "Mute", InputIndex = 0, Offset = 100, ObjectType = DeviceObjectTypeFlags.PushButton, ObjectTypeGuid = ObjectGuid.Button },
                new DeviceObjectItem { Name = "Play/Pause", InputIndex = 1, Offset = 101, ObjectType = DeviceObjectTypeFlags.PushButton, ObjectTypeGuid = ObjectGuid.Button },
                new DeviceObjectItem { Name = "Volume Up", InputIndex = 2, Offset = 102, ObjectType = DeviceObjectTypeFlags.PushButton, ObjectTypeGuid = ObjectGuid.Button },
                new DeviceObjectItem { Name = "Consumer 0x0199", InputIndex = 3, Offset = 103, ObjectType = DeviceObjectTypeFlags.PushButton, ObjectTypeGuid = ObjectGuid.Button },
            },
        };

        /// <summary>The aux (left-side) accelerometer capability (#199/#208) rides
        /// caps bit 128 in the device list. It must survive the round-trip in both
        /// states, or the consumer's 'Motion Accel L' source stays un-pickable for a
        /// shared Nunchuk / left Joy-Con (the flag also drives RemotePeerDevice, whose
        /// HasAccelAux the mapping picker gates on).</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AccelAuxCapability_RoundTrips(bool hasAccelAux)
        {
            var info = new RemotePeerDeviceInfo
            {
                Slot = 2,
                PeerLocalDeviceId = "nunchuk-pad",
                Name = "Wii Remote + Nunchuk",
                VendorId = 0x057E,
                ProductId = 0x0306,
                NumAxes = 6,
                NumButtons = 15,
                InputDeviceType = InputDeviceType.Gamepad,
                HasGyro = true,
                HasAccel = true,
                HasAccelAux = hasAccelAux,
            };

            var d = LinkConnection.DecodeDeviceList(LinkConnection.EncodeDeviceList(new[] { info }))[0];
            Assert.Equal(hasAccelAux, d.HasAccelAux);
            // The other caps in the same byte must not be disturbed by bit 128.
            Assert.True(d.HasGyro);
            Assert.True(d.HasAccel);

            d.PeerFingerprintHex = "AB12";
            var dev = new RemotePeerDevice(d);
            Assert.Equal(hasAccelAux, dev.HasAccelAux);
        }

        [Fact]
        public void MetadataExtension_RoundTrips()
        {
            var bytes = LinkConnection.EncodeDeviceList(new[] { ConsumerInfo() });
            var list = LinkConnection.DecodeDeviceList(bytes);

            Assert.Single(list);
            var d = list[0];
            Assert.Equal("serial-42", d.SerialNumber);
            Assert.Equal(2, d.NumTouchpads);
            Assert.Equal(new[] { 5, 2 }, d.TouchpadFingerCounts);
            Assert.NotNull(d.DeviceObjects);
            Assert.Equal(4, d.DeviceObjects.Length);
            Assert.Equal("Mute", d.DeviceObjects[0].Name);
            Assert.Equal("Play/Pause", d.DeviceObjects[1].Name);
            Assert.Equal(1, d.DeviceObjects[1].InputIndex);
            Assert.Equal(101, d.DeviceObjects[1].Offset);
            Assert.Equal(DeviceObjectTypeFlags.PushButton, d.DeviceObjects[1].ObjectType);
            Assert.Equal(ObjectGuid.Button, d.DeviceObjects[1].ObjectTypeGuid);
        }

        [Fact]
        public void RemotePeerDevice_ExposesForwardedNamesNotSynthesized()
        {
            var bytes = LinkConnection.EncodeDeviceList(new[] { ConsumerInfo() });
            var d = LinkConnection.DecodeDeviceList(bytes)[0];
            d.PeerFingerprintHex = "AB12";
            var dev = new RemotePeerDevice(d);

            var objs = dev.GetDeviceObjects();
            Assert.Equal("Mute", objs.First(o => o.InputIndex == 0 && o.IsButton).Name);
            Assert.Equal(2, dev.NumTouchpads);
            Assert.Equal("serial-42", dev.SerialNumber);
        }

        /// <summary>An old sender's payload ends at the v1 records. The decoder
        /// must return the v1 fields and leave the metadata at defaults (the
        /// consumer then synthesizes objects exactly as before).</summary>
        [Fact]
        public void V1OnlyPayload_DecodesWithoutExtension()
        {
            // Hand-rolled v1 record, mirroring the pre-extension layout.
            var buf = new List<byte> { 1, 7 }; // count=1, slot=7
            void Str(string s)
            {
                var b = System.Text.Encoding.UTF8.GetBytes(s);
                buf.Add((byte)(b.Length & 0xFF)); buf.Add((byte)(b.Length >> 8));
                buf.AddRange(b);
            }
            void U16(ushort v) { buf.Add((byte)(v & 0xFF)); buf.Add((byte)(v >> 8)); }
            Str("old-pad");
            Str("Old Pad");
            U16(0x054C); U16(0x0CE6);
            buf.Add(6); buf.Add(17); buf.Add(1);   // axes, buttons, hats
            buf.Add(1 | 64);                        // rumble + online
            U16((ushort)InputDeviceType.Gamepad);

            var list = LinkConnection.DecodeDeviceList(buf.ToArray());
            Assert.Single(list);
            Assert.Equal("Old Pad", list[0].Name);
            Assert.Equal(17, list[0].NumButtons);
            Assert.Null(list[0].DeviceObjects);
            Assert.Equal(0, list[0].NumTouchpads);
        }

        /// <summary>A corrupt extension tail must never cost the v1 records:
        /// the decoder falls back to the v1 result with no objects.</summary>
        [Fact]
        public void MalformedExtension_FallsBackToV1()
        {
            var info = ConsumerInfo();
            var good = LinkConnection.EncodeDeviceList(new[] { info });

            // Locate the extension start by encoding the same record without
            // metadata: the v1 section is byte-identical, so its length is the
            // extension offset in the full payload.
            info.SerialNumber = ""; info.NumTouchpads = 0;
            info.TouchpadFingerCounts = null; info.DeviceObjects = null;
            var bare = LinkConnection.EncodeDeviceList(new[] { info });

            // The bare payload's tail is a fixed 12 bytes: the v1 ext
            // [magic][serial len=0 (2B)][pads=0][objCount=0 (2B)] = 6, the
            // v2 ext [magic][rawButtonCount=0] = 2, the v3 ext
            // [magic][caps2=0] = 2 (#241 NFC capability), and the v4 ext
            // [magic][rawAxisCount=0] = 2 (#193 over the wire). The v1
            // section length falls out of it.
            int v1Len = bare.Length - 12;
            Assert.Equal(0xE2, bare[v1Len]);

            var corrupt = new byte[v1Len + 3];
            Array.Copy(good, corrupt, v1Len);
            corrupt[v1Len] = 0xE2;      // magic present...
            corrupt[v1Len + 1] = 0xFF;  // ...followed by a truncated garbage tail
            corrupt[v1Len + 2] = 0xFF;

            var list = LinkConnection.DecodeDeviceList(corrupt);
            Assert.Single(list);
            Assert.Equal("All Consumer Controls (Merged)", list[0].Name);
            Assert.Null(list[0].DeviceObjects);
        }

        /// <summary>A gamepad with more raw HID buttons than the 22 standardized
        /// slots carries the raw count over the v2 tail, and the consumer offers
        /// every raw button in its synthesized object list.</summary>
        [Fact]
        public void RawButtonCount_RoundTripsAndSynthesizesExtras()
        {
            var info = new RemotePeerDeviceInfo
            {
                Slot = 1,
                PeerLocalDeviceId = "raw-stick",
                Name = "Fight Stick",
                NumAxes = 6,
                NumButtons = 22,      // SDL gamepad standard slots
                RawButtonCount = 26,  // four extra native buttons
                NumHats = 1,
                InputDeviceType = InputDeviceType.Gamepad,
            };

            var d = LinkConnection.DecodeDeviceList(LinkConnection.EncodeDeviceList(new[] { info }))[0];
            Assert.Equal(26, d.RawButtonCount);

            d.PeerFingerprintHex = "AB12";
            var dev = new RemotePeerDevice(d);
            Assert.Equal(26, dev.RawButtonCount);
            var buttonObjs = dev.GetDeviceObjects().Where(o => o.IsButton).ToList();
            Assert.Equal(26, buttonObjs.Count);
            Assert.Contains(buttonObjs, o => o.InputIndex == 25);
        }

        /// <summary>A device with no extras carries RawButtonCount 0 over the
        /// wire (same value an old peer's absent tail decodes to); the consumer
        /// maxes it with NumButtons so nothing regresses. The genuinely-absent
        /// tail path is covered by V1OnlyPayload_DecodesWithoutExtension.</summary>
        [Fact]
        public void ZeroRawButtonCount_FallsBackToNumButtons()
        {
            var info = new RemotePeerDeviceInfo
            {
                PeerLocalDeviceId = "pad", Name = "Pad",
                NumButtons = 22, InputDeviceType = InputDeviceType.Gamepad,
            };
            var d = LinkConnection.DecodeDeviceList(LinkConnection.EncodeDeviceList(new[] { info }))[0];
            Assert.Equal(0, d.RawButtonCount); // wire default when no extras

            d.PeerFingerprintHex = "AB12";
            var dev = new RemotePeerDevice(d);
            Assert.Equal(22, dev.RawButtonCount); // maxed with NumButtons
        }
    }
}
