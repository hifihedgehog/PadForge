using System;
using System.Threading;
using HIDMaestro;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A profile import reloads the catalog on the UI thread while a
    /// lifecycle task's CreateHMaestroController looks a profile up by id
    /// with no lock. The reload emptied the id map before it built the new
    /// one, so a lookup that had already found the catalog loaded read the
    /// empty map and answered null for a profile both catalogs hold.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class HMaestroProfileCatalogReloadTests
    {
        [Fact]
        public void ALookupRacingAReloadFindsTheProfile()
        {
            string id = HMaestroProfileCatalog.CustomProfileId;
            Assert.NotNull(HMaestroProfileCatalog.GetProfileById(id));

            var savedProvider = HMaestroProfileCatalog.UserProfilesProvider;
            var reader = Thread.CurrentThread;
            Thread reloader = null;
            var loading = new ManualResetEventSlim(false);
            var read = new ManualResetEventSlim(false);
            HMProfile found = null;

            // The reload asks for the user's profiles while it builds the
            // new catalog. There it waits for the lookup to finish.
            HMaestroProfileCatalog.UserProfilesProvider = () =>
            {
                var profiles = savedProvider?.Invoke();
                if (Thread.CurrentThread == reloader)
                {
                    loading.Set();
                    read.Wait(10000);
                }
                return profiles;
            };
            // The lookup has taken the catalog. A reload starts and is
            // building the new one when the lookup reads.
            HMaestroProfileCatalog.LookupSeam = () =>
            {
                if (Thread.CurrentThread != reader || reloader != null) return;
                reloader = new Thread(HMaestroProfileCatalog.Reload) { IsBackground = true, Name = "test reload" };
                reloader.Start();
                loading.Wait(10000);
            };
            try
            {
                found = HMaestroProfileCatalog.GetProfileById(id);
            }
            finally
            {
                HMaestroProfileCatalog.LookupSeam = null;
                read.Set();
                reloader?.Join(30000);
                HMaestroProfileCatalog.UserProfilesProvider = savedProvider;
            }

            Assert.True(loading.IsSet, "the reload never reached its build");
            Assert.NotNull(found);
            Assert.Equal(id, found.Id);
            // The reload finished and published a catalog with the profile.
            Assert.NotNull(HMaestroProfileCatalog.GetProfileById(id));
        }

        /// <summary>Manage Profiles builds the catalog off the UI thread
        /// and publishes it after the store changed. A build made from the
        /// store as it is now is published as it is, and one the store moved
        /// past is built again, so the published catalog always holds the
        /// store's profiles.</summary>
        [Fact]
        public void APublishedBuildHoldsTheStoresProfiles()
        {
            const string id = "padforge-test-publish-user";
            string json = UserProfileProblemTests.ProfileJson(id);
            var savedProvider = HMaestroProfileCatalog.UserProfilesProvider;
            try
            {
                HMaestroProfileCatalog.UserProfilesProvider = () => new[] { json };
                HMaestroProfileCatalog.Publish(HMaestroProfileCatalog.Build(new[] { json }));
                Assert.NotNull(HMaestroProfileCatalog.GetProfileById(id));

                // A build from before the store took the profile.
                HMaestroProfileCatalog.UserProfilesProvider = () => System.Array.Empty<string>();
                HMaestroProfileCatalog.Reload();
                var stale = HMaestroProfileCatalog.Build(System.Array.Empty<string>());
                HMaestroProfileCatalog.UserProfilesProvider = () => new[] { json };
                HMaestroProfileCatalog.Publish(stale);
                Assert.NotNull(HMaestroProfileCatalog.GetProfileById(id));
            }
            finally
            {
                HMaestroProfileCatalog.UserProfilesProvider = savedProvider;
                HMaestroProfileCatalog.Reload();
            }
        }
    }
}
