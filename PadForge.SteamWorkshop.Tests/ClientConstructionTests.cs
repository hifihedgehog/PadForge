using System;
using System.Threading.Tasks;
using PadForge.SteamWorkshop;
using PadForge.SteamWorkshop.Api;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// The opt-in gate is the feature's hard privacy boundary: with it off, no client can be
    /// constructed, so no Steam network path can run. These tests exercise only construction;
    /// there are no live-network tests in the suite.
    /// </summary>
    public class ClientConstructionTests
    {
        private sealed class FakeGate : ISteamWorkshopGate
        {
            public bool IsCommunityConfigLookupEnabled { get; set; }
        }

        private static ISteamWorkshopGate On() => new FakeGate { IsCommunityConfigLookupEnabled = true };

        private static ISteamWorkshopGate Off() => new FakeGate { IsCommunityConfigLookupEnabled = false };

        [Fact]
        public void StoreClient_throws_when_disabled() =>
            Assert.Throws<InvalidOperationException>(() => new SteamStoreClient(Off()));

        [Fact]
        public void CommunityClient_throws_when_disabled() =>
            Assert.Throws<InvalidOperationException>(() => new SteamCommunityClient(Off()));

        [Fact]
        public void RemoteStorageClient_throws_when_disabled() =>
            Assert.Throws<InvalidOperationException>(() => new SteamRemoteStorageClient(Off()));

        [Fact]
        public void UgcDownloader_throws_when_disabled() =>
            Assert.Throws<InvalidOperationException>(() => new SteamUgcDownloader(Off()));

        [Fact]
        public void ArtworkClient_throws_when_disabled() =>
            Assert.Throws<InvalidOperationException>(() => new SteamArtworkClient(Off()));

        [Fact]
        public void WorkshopClient_throws_when_disabled() =>
            Assert.Throws<InvalidOperationException>(() => new SteamWorkshopClient(Off()));

        [Fact]
        public void HttpsClients_construct_when_enabled()
        {
            Assert.NotNull(new SteamStoreClient(On()));
            Assert.NotNull(new SteamCommunityClient(On()));
            Assert.NotNull(new SteamRemoteStorageClient(On()));
            Assert.NotNull(new SteamUgcDownloader(On()));
            Assert.NotNull(new SteamArtworkClient(On()));
        }

        [Fact]
        public async Task WorkshopClient_constructs_and_disposes_without_network()
        {
            await using var client = new SteamWorkshopClient(On());
            Assert.NotNull(client);
        }

        [Fact]
        public void All_clients_throw_on_null_gate()
        {
            Assert.Throws<ArgumentNullException>(() => new SteamStoreClient(null));
            Assert.Throws<ArgumentNullException>(() => new SteamCommunityClient(null));
            Assert.Throws<ArgumentNullException>(() => new SteamRemoteStorageClient(null));
            Assert.Throws<ArgumentNullException>(() => new SteamUgcDownloader(null));
            Assert.Throws<ArgumentNullException>(() => new SteamArtworkClient(null));
            Assert.Throws<ArgumentNullException>(() => new SteamWorkshopClient(null));
        }

        [Fact]
        public void DelegateGate_reflects_the_func_and_rejects_null()
        {
            var enabled = false;
            var gate = new DelegateSteamWorkshopGate(() => enabled);

            Assert.False(gate.IsCommunityConfigLookupEnabled);
            enabled = true;
            Assert.True(gate.IsCommunityConfigLookupEnabled);

            Assert.Throws<ArgumentNullException>(() => new DelegateSteamWorkshopGate(null));
        }

        [Fact]
        public void Disabled_delegate_gate_makes_a_client_throw()
        {
            var gate = new DelegateSteamWorkshopGate(() => false);
            Assert.Throws<InvalidOperationException>(() => new SteamStoreClient(gate));
        }
    }
}
