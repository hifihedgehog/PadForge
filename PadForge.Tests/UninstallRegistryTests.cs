using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Uninstall registrations are read once and read again after
    /// Windows says the keys changed.
    ///
    /// <para>The status timer walked every subkey of both Uninstall keys
    /// twice every five seconds on the interface thread, to show HidHide and
    /// Windows MIDI Services: thousands of registry handles a minute
    /// (measured 2026-11-16).</para>
    /// </summary>
    public class UninstallRegistryTests
    {
        /// <summary>The snapshot holds what a walk of both views reads now,
        /// in the walk's order.</summary>
        [Fact]
        public void TheSnapshotMatchesAWalkOfBothViews()
        {
            var registry = new UninstallRegistry(RegistryHive.LocalMachine, UninstallRegistry.UninstallPath);
            var snapshot = registry.Entries.Select(e => (e.SubKeyName, e.DisplayName, e.DisplayVersion, e.UninstallString)).ToList();
            var walked = Walk().ToList();
            // An installer that ran between the two reads moves both, so the
            // snapshot is read once more when they differ.
            if (!snapshot.SequenceEqual(walked))
                snapshot = registry.Entries.Select(e => (e.SubKeyName, e.DisplayName, e.DisplayVersion, e.UninstallString)).ToList();
            Assert.Equal(walked, snapshot);
            Assert.NotEmpty(snapshot);
        }

        /// <summary>With nothing installed or removed, a read answers from
        /// the snapshot and walks no key.</summary>
        [Fact]
        public void AReadWithNoChangeWalksNothing()
        {
            var registry = new UninstallRegistry(RegistryHive.LocalMachine, UninstallRegistry.UninstallPath);
            var reads = new List<IReadOnlyList<UninstallRegistry.Entry>>();
            for (int i = 0; i < 4; i++) reads.Add(registry.Entries);
            // A program installed while the reads ran moves one pair at most.
            int same = 0;
            for (int i = 1; i < reads.Count; i++)
                if (ReferenceEquals(reads[i], reads[i - 1])) same++;
            Assert.True(same >= 2, "the snapshot was walked again with nothing changed");
            Assert.True(registry.Walks <= 2, $"{registry.Walks} walks for four reads");
        }

        private static IEnumerable<(string, string, string, string)> Walk()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(UninstallRegistry.UninstallPath, false);
                if (key == null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name, false);
                    if (sub == null) continue;
                    yield return (name, sub.GetValue("DisplayName") as string, sub.GetValue("DisplayVersion") as string,
                        sub.GetValue("UninstallString") as string);
                }
            }
        }
    }
}
