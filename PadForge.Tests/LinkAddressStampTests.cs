using System;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Bluetooth address the idle disconnect targets, and how it lands on
    /// a row. Two defects framed these rules, both from the audit of the
    /// change that introduced them.
    ///
    /// <para>The stamp used to return on the FIRST row that already carried a
    /// serial, so a stale sibling row for the same model aborted the whole
    /// stamp and the live row never received an address. And it never
    /// overwrote, so a row that had adopted a different unit of the same model
    /// (Step 1's drawer case, where identity follows connection order by
    /// design) kept the other unit's address and the disconnect would have
    /// targeted the wrong pad.</para>
    /// </summary>
    /// <para>The stamp walks the shared SettingsManager registry, so these
    /// join the SettingsManagerStatics collection and restore what they
    /// replaced, the same discipline the other statics-touching suites
    /// follow.</para>
    [Collection("SettingsManagerStatics")]
    public class LinkAddressStampTests : IDisposable
    {
        private readonly PadForge.Common.Input.DeviceCollection _saved;

        public LinkAddressStampTests()
        {
            _saved = PadForge.Common.Input.SettingsManager.UserDevices;
            PadForge.Common.Input.SettingsManager.UserDevices =
                new PadForge.Common.Input.DeviceCollection();
        }

        public void Dispose()
            => PadForge.Common.Input.SettingsManager.UserDevices = _saved;

        private const ushort Vid = 0x054C, Pid = 0x0268;
        private const string MacA = "00265c507543";
        private const string MacB = "0007040a1b7a";

        private static UserDevice Row(string serial, ushort pid = Pid)
            => new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                VendorId = Vid,
                ProdId = pid,
                SerialNumber = serial,
                CapType = InputDeviceType.Gamepad,
            };

        /// <summary>Populates the shared registry the stamp walks, and hands
        /// back the rows so a test can assert on them.</summary>
        private static UserDevice[] Seed(params UserDevice[] rows)
        {
            var devices = PadForge.Common.Input.SettingsManager.UserDevices;
            Assert.NotNull(devices);
            lock (devices.SyncRoot)
            {
                devices.Items.Clear();
                foreach (var r in rows) devices.Items.Add(r);
            }
            return rows;
        }

        /// <summary>The ordinary case: one row, no address, stamp lands.</summary>
        [Fact]
        public void ASingleAddresslessRow_GetsTheAddress()
        {
            var rows = Seed(Row(""));
            PadForge.Services.Ds3PairingService.StampLinkAddress(Vid, Pid, MacA);
            Assert.Equal(MacA, rows[0].SerialNumber);
        }

        /// <summary>THE ABORT BUG. A stale sibling that already carries an
        /// address must not stop the live row from receiving one.</summary>
        [Fact]
        public void AStaleSiblingRow_DoesNotAbortTheStamp()
        {
            var rows = Seed(Row(MacB), Row(""));
            PadForge.Services.Ds3PairingService.StampLinkAddress(Vid, Pid, MacA);
            Assert.Equal(MacB, rows[0].SerialNumber);   // untouched
            Assert.Equal(MacA, rows[1].SerialNumber);   // stamped
        }

        /// <summary>A non-authoritative source never corrects an address that
        /// is already there. The pairing record and the dock read cannot tell
        /// which unit is on the air.</summary>
        [Fact]
        public void ANonAuthoritativeSource_NeverOverwrites()
        {
            var rows = Seed(Row(MacB));
            PadForge.Services.Ds3PairingService.StampLinkAddress(Vid, Pid, MacA);
            Assert.Equal(MacB, rows[0].SerialNumber);
        }

        /// <summary>THE STALE-ADDRESS BUG. The device node belongs to the
        /// connection being served, so it may correct a row that adopted the
        /// other unit of the same model.</summary>
        [Fact]
        public void TheAuthoritativeSource_CorrectsAStaleAddress()
        {
            var rows = Seed(Row(MacB));
            PadForge.Services.Ds3PairingService.StampLinkAddress(
                Vid, Pid, MacA, authoritative: true);
            Assert.Equal(MacA, rows[0].SerialNumber);
        }

        /// <summary>Re-stamping the same address is a no-op either way, so a
        /// reconnect does not churn the registry signature.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RestampingTheSameAddress_ChangesNothing(bool authoritative)
        {
            var rows = Seed(Row(MacA));
            PadForge.Services.Ds3PairingService.StampLinkAddress(
                Vid, Pid, MacA, authoritative);
            Assert.Equal(MacA, rows[0].SerialNumber);
        }

        /// <summary>Two addressless rows of one model cannot be told apart, so
        /// neither is stamped. A wrong address disconnects the wrong pad.</summary>
        [Fact]
        public void TwoAddresslessRowsOfOneModel_AreLeftAlone()
        {
            var rows = Seed(Row(""), Row(""));
            PadForge.Services.Ds3PairingService.StampLinkAddress(Vid, Pid, MacA);
            Assert.Equal("", rows[0].SerialNumber);
            Assert.Equal("", rows[1].SerialNumber);
        }

        /// <summary>WHICH SOURCE MAY CORRECT. The device node belongs to the
        /// connection being served, so it passes authoritative. The pairing
        /// record and the dock read cannot tell which unit is on the air, so
        /// they must not.
        ///
        /// <para>Asserted against the source text because the device-node read
        /// needs a real PnP node and cannot run in-process. The behaviour
        /// either side of that flag is covered by the tests above; this pins
        /// the wiring that chooses it, which a mutation of the call site
        /// otherwise slips past.</para></summary>
        [Fact]
        public void OnlyTheDeviceNodeSource_ClaimsAuthority()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "PadForge.App", "Services", "Ds3PairingService.cs"));

            int node = src.IndexOf("StampLinkAddressFromDeviceNode(", StringComparison.Ordinal);
            int record = src.IndexOf("StampLinkAddressFromPairingRecord(", StringComparison.Ordinal);
            Assert.True(node > 0 && record > 0);

            string nodeBody = Body(src, "internal static void StampLinkAddressFromDeviceNode");
            string recordBody = Body(src, "internal static void StampLinkAddressFromPairingRecord");

            Assert.Contains("authoritative: true", nodeBody, StringComparison.Ordinal);
            Assert.DoesNotContain("authoritative", recordBody, StringComparison.Ordinal);
        }

        private static string Body(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at > 0, signature + " not found");
            int next = src.IndexOf("\n        internal static", at + 1, StringComparison.Ordinal);
            if (next < 0) next = src.IndexOf("\n        private static", at + 1, StringComparison.Ordinal);
            return next > at ? src.Substring(at, next - at) : src.Substring(at);
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(
                System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        /// <summary>A different model is never touched.</summary>
        [Fact]
        public void ADifferentModel_IsNotStamped()
        {
            var rows = Seed(Row("", pid: 0x042F));
            PadForge.Services.Ds3PairingService.StampLinkAddress(Vid, Pid, MacA);
            Assert.Equal("", rows[0].SerialNumber);
        }
    }
}
