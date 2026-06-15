using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public class LinkDiscoveryTests
    {
        [Fact]
        public void Beacon_RoundTrips()
        {
            var bytes = LinkDiscovery.BuildBeacon(27500, "BOBS-LAPTOP", "AABBCCDD11223344");
            Assert.True(LinkDiscovery.TryParseBeacon(bytes, out int port, out string name, out string fp));
            Assert.Equal(27500, port);
            Assert.Equal("BOBS-LAPTOP", name);
            Assert.Equal("AABBCCDD11223344", fp);
        }

        [Fact]
        public void Beacon_HandlesEmptyNameAndFingerprint()
        {
            var bytes = LinkDiscovery.BuildBeacon(1024, "", "");
            Assert.True(LinkDiscovery.TryParseBeacon(bytes, out int port, out string name, out string fp));
            Assert.Equal(1024, port);
            Assert.Equal("", name);
            Assert.Equal("", fp);
        }

        [Theory]
        [InlineData(new byte[] { 0x00 })]
        [InlineData(new byte[] { (byte)'P', (byte)'F', (byte)'L', (byte)'K', 99 })] // wrong version
        [InlineData(new byte[] { (byte)'X', (byte)'X', (byte)'X', (byte)'X', 1, 0, 0, 0 })] // wrong magic
        public void Beacon_RejectsMalformed(byte[] data)
        {
            Assert.False(LinkDiscovery.TryParseBeacon(data, out _, out _, out _));
        }

        [Fact]
        public void Beacon_RejectsTruncatedNameLength()
        {
            // Valid header claiming a 200-byte name but no bytes follow.
            var good = LinkDiscovery.BuildBeacon(5000, "x", "ab");
            var truncated = good[..(good.Length - 1)]; // drop the last name byte
            Assert.False(LinkDiscovery.TryParseBeacon(truncated, out _, out _, out _));
        }

        [Fact]
        public void Beacon_ClampsOverlongName()
        {
            string longName = new string('Z', 300);
            var bytes = LinkDiscovery.BuildBeacon(27500, longName, "aa");
            Assert.True(LinkDiscovery.TryParseBeacon(bytes, out _, out string name, out _));
            Assert.True(name.Length <= 64);
        }
    }
}
