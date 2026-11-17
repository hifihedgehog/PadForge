using System;
using System.Linq;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A profile is found by its ID through an index built with the catalog.
    ///
    /// <para>The UI timer looks up each slot's profile 30 times a second,
    /// and the scan it replaced made a closure and a delegate each time, the
    /// most numerous garbage in the program at rest (measured 2026-11-16).
    /// Every collection of that garbage stops the poll loop's thread
    /// too.</para>
    /// </summary>
    public class ProfileLookupTests
    {
        /// <summary>The first profile in catalog order wins where two share
        /// an ID, as the scan's first match did, in any case.</summary>
        [Fact]
        public void EveryProfileIsFoundByItsIdInAnyCase()
        {
            var all = HMaestroProfileCatalog.AllProfiles;
            Assert.NotEmpty(all);
            foreach (var profile in all)
            {
                var first = all.First(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
                Assert.Same(first, HMaestroProfileCatalog.GetProfileById(profile.Id));
                Assert.Same(first, HMaestroProfileCatalog.GetProfileById(profile.Id.ToUpperInvariant()));
            }
            Assert.Null(HMaestroProfileCatalog.GetProfileById("no-such-profile"));
            Assert.Null(HMaestroProfileCatalog.GetProfileById(null));
            Assert.Null(HMaestroProfileCatalog.GetProfileById(string.Empty));
        }

        [Fact]
        public void LookingUpAProfileAllocatesNothing()
        {
            string id = HMaestroProfileCatalog.AllProfiles[0].Id;
            HMaestroProfileCatalog.GetProfileById(id);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) HMaestroProfileCatalog.GetProfileById(id);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }
}
