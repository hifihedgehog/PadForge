using System;
using System.IO;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Parked per-device settings survive a load (#404, discussion #395).
    ///
    /// <para>A UserSetting whose MapTo is -1 is NOT residue. ApplyProfile
    /// parks every device the incoming profile does not assign at -1 and
    /// KEEPS its PadSetting, so switching back restores it
    /// (InputService.ApplyProfile). Those rows carry the user's mappings:
    /// every Map element in a stored profile lives on a PadSetting reached
    /// from an entry by checksum.</para>
    ///
    /// <para>The load used to drop every row with MapTo below zero as an
    /// "orphaned UserSetting left by older versions". That sweep began as a
    /// migration for files an old DeleteSlot had written (bc456e3c) and
    /// outlived its purpose. A Flydigi Vader 5 Pro owner lost three parked
    /// entries and 53 mappings on every restart, and the autosave then wrote
    /// the loss over the stored profile, which is why Load could not bring
    /// them back and only a re-import could.</para>
    ///
    /// <para>The buffer overflow that motivated the original sweep is
    /// guarded by the lookup itself, which filters MapTo >= 0
    /// (SettingsManager.FindByInstanceGuid), not by deleting rows.</para>
    /// </summary>
    public class ParkedDeviceSettingTests
    {

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static UserSetting Row(int mapTo, bool withPadSetting)
        {
            var us = new UserSetting { MapTo = mapTo };
            if (withPadSetting) us.SetPadSetting(new PadSetting());
            return us;
        }

        /// <summary>THE BUG. A parked row carrying the user's settings is
        /// authoring, and the load must keep it.</summary>
        [Fact]
        public void ParkedRowWithSettings_Survives()
        {
            Assert.False(SettingsService.IsEmptyLegacyOrphan(Row(-1, withPadSetting: true)));
        }

        /// <summary>The row the sweep was actually written for: parked off
        /// every slot and carrying nothing at all.</summary>
        [Fact]
        public void ParkedRowWithNothing_IsStillDropped()
        {
            Assert.True(SettingsService.IsEmptyLegacyOrphan(Row(-1, withPadSetting: false)));
        }

        /// <summary>An assigned row is never a candidate, with or without a
        /// settings object.</summary>
        [Theory]
        [InlineData(0, true)]
        [InlineData(0, false)]
        [InlineData(3, true)]
        [InlineData(15, false)]
        public void AssignedRow_IsNeverDropped(int slot, bool withPadSetting)
        {
            Assert.False(SettingsService.IsEmptyLegacyOrphan(Row(slot, withPadSetting)));
        }

        /// <summary>A null entry is dropped rather than thrown on, since the
        /// sweep runs over a list the polling thread also walks.</summary>
        [Fact]
        public void NullRow_IsDropped()
        {
            Assert.True(SettingsService.IsEmptyLegacyOrphan(null));
        }

        /// <summary>The load must call the emptiness predicate, not test
        /// MapTo directly. Pinned against the source so a future edit cannot
        /// quietly restore the MapTo rule that caused the data loss.</summary>
        [Fact]
        public void LoadPurges_OnEmptiness_NotOnMapTo()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Services", "SettingsService.cs"));
            Assert.Contains("RemoveAll(IsEmptyLegacyOrphan)", src);
            Assert.DoesNotContain("RemoveAll(us => us.MapTo < 0)", src);
        }
    }
}
