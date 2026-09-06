using System;
using System.Buffers.Binary;
using System.Reflection;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Services;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public class DsuMotionFrameTests
    {
        // DS4Windows DS4Sixaxis.handleSixaxis/populate and UdpServer:
        // SDL (x,y,z) becomes DSU (pitch,yaw,roll) = (x,-y,-z).
        // Eden udp_client.cpp decodes that as (pitch,roll,-yaw), matching
        // its sdl_driver.cpp axes (x,-z,y). Eden's UDP rate scale differs.
        [Theory]
        [InlineData(25f, 0f, 0f, 25f, 0f, 0f)]
        [InlineData(-25f, 0f, 0f, -25f, 0f, 0f)]
        [InlineData(0f, 25f, 0f, 0f, -25f, 0f)]
        [InlineData(0f, -25f, 0f, 0f, 25f, 0f)]
        [InlineData(0f, 0f, 25f, 0f, 0f, -25f)]
        [InlineData(0f, 0f, -25f, 0f, 0f, 25f)]
        public void Packet_UsesCemuhookAxesForBothGyroDirections(
            float x, float y, float z, float pitch, float yaw, float roll)
        {
            var snapshot = new MotionSnapshot
            {
                AccelX = 0.25f, AccelY = 0.5f, AccelZ = 0.75f,
                GyroPitch = x, GyroYaw = y, GyroRoll = z,
                TimestampUs = 0x0102030405060708L, HasMotion = true,
            };
            using var server = new DsuMotionServer();
            byte[] packet = BuildPacket(server, snapshot);

            Assert.Equal(100, packet.Length);
            Assert.Equal(0x100002u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)));
            Assert.Equal(snapshot.TimestampUs, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(68)));
            Assert.Equal(-0.25f, ReadFloat(packet, 76));
            Assert.Equal(-0.5f, ReadFloat(packet, 80));
            Assert.Equal(-0.75f, ReadFloat(packet, 84));
            Assert.Equal(pitch, ReadFloat(packet, 88));
            Assert.Equal(yaw, ReadFloat(packet, 92));
            Assert.Equal(roll, ReadFloat(packet, 96));
        }

        [Theory]
        [InlineData("Pointing", 0.2f, -0.4f, 0.6f)]
        [InlineData("Sideways", 0.6f, -0.4f, -0.2f)]
        [InlineData("WiiWheel", 0.6f, 0.2f, -0.4f)]
        [InlineData("Upright", 0.2f, -0.6f, -0.4f)]
        public void CalibratedGrip_PreservesAxesThroughDsuEncoding(
            string grip, float expectedX, float expectedY, float expectedZ)
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            try
            {
                SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning
                {
                    Grip = grip, ApplyToPassthrough = false,
                    Space = "World", SensH = 0.25f, SensV = 0.5f,
                };
                SourceCoercion.GyroBiasProvider = (_, _) => (0.1f, -0.2f, 0.3f);
                var state = new CustomInputState();
                state.Gyro[0] = 0.3f;
                state.Gyro[1] = -0.6f;
                state.Gyro[2] = 0.9f;
                SourceCoercion.GetPassthroughGyro(state, "dsu-frame-test", 0,
                    out float pitch, out float yaw, out float roll);
                const float radToDeg = 180f / MathF.PI;
                var snapshot = new MotionSnapshot
                {
                    GyroPitch = pitch * radToDeg,
                    GyroYaw = yaw * radToDeg,
                    GyroRoll = roll * radToDeg,
                    HasMotion = true,
                };
                using var server = new DsuMotionServer();
                byte[] packet = BuildPacket(server, snapshot);

                // Remove degrees/second scaling to compare the sensor frames.
                // Eden's UDP decoder uses pitch, roll, -yaw. Its SDL decoder
                // uses x, -z, y. Neither consumer needs a grip-specific fix.
                Assert.Equal(expectedX, ReadFloat(packet, 88) / radToDeg, 5);
                Assert.Equal(-expectedZ, ReadFloat(packet, 96) / radToDeg, 5);
                Assert.Equal(expectedY, -ReadFloat(packet, 92) / radToDeg, 5);
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
            }
        }

        private static byte[] BuildPacket(DsuMotionServer server, MotionSnapshot snapshot)
        {
            var method = typeof(DsuMotionServer).GetMethod("BuildPadDataPacket",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (byte[])method.Invoke(server, new object[] { 0, snapshot, true });
        }

        private static float ReadFloat(byte[] packet, int offset)
            => BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset, 4));
    }
}
