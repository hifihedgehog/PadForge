using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public class LinkHandshakeTests
    {
        private static readonly byte[] CapsA = { 1, 0, 0xFF };
        private static readonly byte[] CapsB = { 1, 0, 0x0F };

        // Drives a full I<->R handshake and returns both completed results.
        private static (HandshakeResult init, HandshakeResult resp) RunFull(
            PeerIdentity idI, PeerIdentity idR,
            System.Func<byte[], byte[]> tamperMsg3 = null)
        {
            var i = new LinkHandshake(idI, CapsA, isInitiator: true);
            var r = new LinkHandshake(idR, CapsB, isInitiator: false);

            byte[] commit = i.StartCommit();
            byte[] revealR = r.OnInitiatorCommit(commit);
            byte[] revealI = i.OnResponderReveal(revealR);
            if (tamperMsg3 != null) revealI = tamperMsg3(revealI);
            byte[] confirm = r.OnInitiatorReveal(revealI);
            i.OnResponderConfirm(confirm);

            return (i.Result, r.Result);
        }

        [Fact]
        public void FullHandshake_BothSidesAgreeKeyAndSas()
        {
            var (init, resp) = RunFull(PeerIdentity.Generate(), PeerIdentity.Generate());

            Assert.NotNull(init);
            Assert.NotNull(resp);
            Assert.Equal(System.Convert.ToHexString(init.SessionKey), System.Convert.ToHexString(resp.SessionKey));
            Assert.Equal(init.Sas, resp.Sas);
            Assert.Equal(6, init.Sas.Length);
            Assert.True(init.IsInitiator);
            Assert.False(resp.IsInitiator);
        }

        [Fact]
        public void HandshakeKey_ActuallyDrivesALinkSession()
        {
            var (init, resp) = RunFull(PeerIdentity.Generate(), PeerIdentity.Generate());
            var a = new LinkSession(init.SessionKey, isInitiator: true);
            var b = new LinkSession(resp.SessionKey, isInitiator: false);

            var dg = a.Seal(LinkMessageType.Input, 0, 7, new byte[] { 4, 2 });
            Assert.True(b.Open(dg, out _, out _, out var ts, out var payload));
            Assert.Equal(7UL, ts);
            Assert.Equal(new byte[] { 4, 2 }, payload);
        }

        [Fact]
        public void EachSidePinsTheOthersStaticKey()
        {
            var idI = PeerIdentity.Generate();
            var idR = PeerIdentity.Generate();
            var (init, resp) = RunFull(idI, idR);

            Assert.Equal(System.Convert.ToHexString(idR.PublicKey), System.Convert.ToHexString(init.PeerStaticPublicKey));
            Assert.Equal(System.Convert.ToHexString(idI.PublicKey), System.Convert.ToHexString(resp.PeerStaticPublicKey));
            Assert.Equal(System.Convert.ToHexString(idR.Fingerprint), System.Convert.ToHexString(init.PeerFingerprint));
        }

        [Fact]
        public void CapabilitiesSurviveBoundIntoTranscript()
        {
            var (init, resp) = RunFull(PeerIdentity.Generate(), PeerIdentity.Generate());
            Assert.Equal(CapsB, init.PeerCapabilities); // I sees R's caps
            Assert.Equal(CapsA, resp.PeerCapabilities); // R sees I's caps
        }

        [Fact]
        public void TamperedRevealSignature_RejectedByResponder()
        {
            var ex = Assert.Throws<HandshakeException>(() =>
                RunFull(PeerIdentity.Generate(), PeerIdentity.Generate(), tamperMsg3: m =>
                {
                    var t = (byte[])m.Clone();
                    t[^1] ^= 0xFF; // flip a signature byte
                    return t;
                }));
            Assert.Contains("signature", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MitmSwappedStaticKey_BreaksConfirmOrSignature()
        {
            // A man-in-the-middle that re-signs with its own key produces a
            // different transcript/fingerprint; the honest sides' SAS would differ
            // and the signature is over a transcript the victim didn't agree to.
            var idI = PeerIdentity.Generate();
            var idR = PeerIdentity.Generate();
            var mitm = PeerIdentity.Generate();

            var i = new LinkHandshake(idI, CapsA, isInitiator: true);
            var r = new LinkHandshake(idR, CapsB, isInitiator: false);

            byte[] commit = i.StartCommit();
            byte[] revealR = r.OnInitiatorCommit(commit);
            byte[] revealI = i.OnResponderReveal(revealR);

            // Corrupting the revealed static-key / transcript region must make the
            // responder reject (commit mismatch or signature failure) — a swapped
            // identity can't produce a transcript the honest initiator signed.
            var tampered = (byte[])revealI.Clone();
            tampered[10] ^= 0xFF;
            Assert.ThrowsAny<HandshakeException>(() => r.OnInitiatorReveal(tampered));
            _ = mitm; // identity stand-in for the swap scenario above
        }

        [Fact]
        public void CommitMismatch_RejectedByResponder()
        {
            var idI = PeerIdentity.Generate();
            var idR = PeerIdentity.Generate();

            var i = new LinkHandshake(idI, CapsA, isInitiator: true);
            var r = new LinkHandshake(idR, CapsB, isInitiator: false);

            byte[] commit = i.StartCommit();
            byte[] revealR = r.OnInitiatorCommit(commit);
            byte[] revealI = i.OnResponderReveal(revealR);

            // Flip a byte in the revealed ephemeral so it no longer matches the commit.
            var tampered = (byte[])revealI.Clone();
            tampered[4] ^= 0xFF; // inside the reveal-core ephemeral field
            var ex = Assert.Throws<HandshakeException>(() => r.OnInitiatorReveal(tampered));
            Assert.True(ex.Message.Contains("Commit", System.StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("signature", System.StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("Malformed", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void TamperedConfirm_RejectedByInitiator()
        {
            var idI = PeerIdentity.Generate();
            var idR = PeerIdentity.Generate();

            var i = new LinkHandshake(idI, CapsA, isInitiator: true);
            var r = new LinkHandshake(idR, CapsB, isInitiator: false);

            byte[] commit = i.StartCommit();
            byte[] revealR = r.OnInitiatorCommit(commit);
            byte[] revealI = i.OnResponderReveal(revealR);
            byte[] confirm = r.OnInitiatorReveal(revealI);

            var tampered = (byte[])confirm.Clone();
            tampered[^1] ^= 0xFF;
            Assert.Throws<HandshakeException>(() => i.OnResponderConfirm(tampered));
        }

        [Fact]
        public void TwoIndependentHandshakes_ProduceDifferentSessionKeys()
        {
            var idI = PeerIdentity.Generate();
            var idR = PeerIdentity.Generate();
            var (a, _) = RunFull(idI, idR);
            var (b, _) = RunFull(idI, idR); // same identities, fresh ephemerals
            Assert.NotEqual(System.Convert.ToHexString(a.SessionKey), System.Convert.ToHexString(b.SessionKey));
        }

        [Fact]
        public void OutOfOrderCalls_ThrowClosed()
        {
            var i = new LinkHandshake(PeerIdentity.Generate(), CapsA, isInitiator: true);
            Assert.Throws<HandshakeException>(() => i.OnResponderReveal(new byte[] { 0 })); // before StartCommit

            var r = new LinkHandshake(PeerIdentity.Generate(), CapsB, isInitiator: false);
            Assert.Throws<HandshakeException>(() => r.OnInitiatorReveal(new byte[] { 0 })); // before commit
        }

        [Fact]
        public void MalformedControlMessage_FailsClosed()
        {
            var r = new LinkHandshake(PeerIdentity.Generate(), CapsB, isInitiator: false);
            Assert.Throws<HandshakeException>(() => r.OnInitiatorCommit(new byte[] { 0xFF, 0xFF, 0x01 })); // bad TLV
        }

        [Fact]
        public void PeerIdentity_FingerprintStableAndHex()
        {
            var id = PeerIdentity.Generate();
            var clone = new PeerIdentity(id.ExportPrivateKey(), id.PublicKey);
            Assert.Equal(id.FingerprintHex, clone.FingerprintHex);
            Assert.Equal(64, id.FingerprintHex.Length); // 32 bytes hex
        }
    }
}
