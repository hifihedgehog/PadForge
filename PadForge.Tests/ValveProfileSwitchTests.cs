using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Models2D;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Switching an Extended slot from one Steam profile to another.
    ///
    /// <para>The three Valve wires are as unlike each other as the two
    /// Switch wires are, and they live under Extended, where the ProfileId
    /// setter did neither of the two things such a change needs. It never
    /// MOVED what was already bound (Nintendo has had that since the wire
    /// stamp landed, gated on the output type, so Valve never saw it), and
    /// it never AUTOMAPPED the targets the new wire adds. Both halves are
    /// gated on the wire stamp so a launch restore still touches
    /// nothing.</para>
    /// </summary>
    // Mutates SettingsManager statics, which are process-global.
    [Collection("SettingsManagerStatics")]
    public class ValveProfileSwitchTests : IDisposable
    {
        private const string Deck = "steam-deck";
        private const string SC2015 = "steam-controller";
        private const string SC2026 = "steam-controller-2";
        private const int Pad = 11;
        private static readonly Guid Dev = new("aaaaaaaa-bbbb-cccc-dddd-eeeeffff0011");

        private readonly SettingsCollection _savedSettings;
        private readonly MappingSet[] _savedSets;
        private readonly DeviceCollection _savedDevices;

        public ValveProfileSwitchTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedSets = SettingsManager.SlotMappingSets;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.SlotMappingSets = _savedSets;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.StampNintendoWire(Pad, null);
        }

        private static DeviceObjectItem Obj(int idx, DeviceObjectTypeFlags kind)
            => new() { InputIndex = idx, ObjectType = kind };

        /// <summary>A slot carrying the given raw bindings under the given
        /// wire, with a gamepad-shaped device assigned so the automap has
        /// something to bind from.</summary>
        private static PadSetting Arrange(string wire, bool withDevice,
            params (string Target, string Source)[] rows)
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();

            if (withDevice)
            {
                var objs = new List<DeviceObjectItem>();
                for (int i = 0; i < 6; i++) objs.Add(Obj(i, DeviceObjectTypeFlags.AbsoluteAxis));
                for (int i = 0; i < 22; i++) objs.Add(Obj(i, DeviceObjectTypeFlags.PushButton));
                objs.Add(Obj(0, DeviceObjectTypeFlags.PointOfViewController));
                var ud = new UserDevice
                {
                    InstanceGuid = Dev,
                    CapType = (int)InputDeviceType.Gamepad,
                    DeviceObjects = objs.ToArray(),
                };
                lock (SettingsManager.UserDevices.SyncRoot)
                    SettingsManager.UserDevices.Items.Add(ud);
            }

            var ps = new PadSetting();
            foreach (var (t, src) in rows) ps.SetRawMapping(t, src);
            ps.FlushRawMappings();
            ps.UpdateChecksum();

            var us = new UserSetting { InstanceGuid = Dev, MapTo = Pad };
            us.SetPadSetting(ps);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(us);

            var ms = new MappingSet();
            foreach (var (t, src) in rows)
                ms.Rows.Add(new MappingRow
                {
                    Target = t,
                    Sources = new List<MappingSource>
                    {
                        new MappingSource { DeviceGuid = Dev.ToString(), Descriptor = src },
                    },
                });
            var sets = new MappingSet[InputManager.MaxPads];
            sets[Pad] = ms;
            SettingsManager.SlotMappingSets = sets;

            SettingsManager.StampNintendoWire(Pad, wire);
            return ps;
        }

        /// <summary>An Extended slot, built BEFORE the arrange. Setting
        /// OutputType assigns that type's default profile, and that
        /// assignment runs through this very setter, so a VM built after the
        /// stamp would consume it before the test ever changed a
        /// profile.</summary>
        private static PadViewModel ExtendedSlot()
            => new PadViewModel(Pad) { OutputType = VirtualControllerType.Extended };

        private static Dictionary<string, string> Raw(PadSetting ps)
            => (ps.RawMappingEntries ?? Array.Empty<RawMappingEntry>())
                .Where(e => !string.IsNullOrEmpty(e.Value))
                .ToDictionary(e => e.Key, e => e.Value);

        /// <summary>Deck to 2015: Steam moves from index 10 to 9, and the
        /// left pad click from 16 to 12. Left alone, index 10 on the 2015
        /// wire is the LEFT GRIP and index 16 is off the end of it.</summary>
        [Fact]
        public void SwitchingValveProfilesMovesTheBindingsToTheNewWire()
        {
            var vm = ExtendedSlot();
            var ps = Arrange(Deck, withDevice: false,
                ("RawBtn10", "Button 11"),     // Deck: Steam
                ("RawBtn16", "Button 17"));    // Deck: left pad click

            vm.ProfileId = SC2015;

            var raw = Raw(ps);
            Assert.Equal("Button 11", raw["RawBtn9"]);    // 2015: Steam
            Assert.Equal("Button 17", raw["RawBtn12"]);   // 2015: left pad click
            Assert.False(raw.ContainsKey("RawBtn10"));
            Assert.False(raw.ContainsKey("RawBtn16"));
        }

        /// <summary>The grid reads MappingSet rows, not PadSetting entries,
        /// so a translation that moved only the settings would leave every
        /// moved binding without a row.</summary>
        [Fact]
        public void TheSlotsMappingRowsMoveWithIt()
        {
            var vm = ExtendedSlot();
            Arrange(Deck, withDevice: false,
                ("RawBtn10", "Button 11"),
                ("RawBtn16", "Button 17"));

            vm.ProfileId = SC2015;

            var targets = SettingsManager.SlotMappingSets[Pad].Rows.Select(r => r.Target).ToList();
            Assert.Contains("RawBtn9", targets);
            Assert.Contains("RawBtn12", targets);
            Assert.DoesNotContain("RawBtn10", targets);
        }

        /// <summary>The 2026 pad's D-pad is four discrete buttons at 18-21.
        /// The Deck's wire ends at 17, so nothing was ever bound there and
        /// only the automap can fill them.</summary>
        [Fact]
        public void SwitchingToAProfileWithMoreControlsAutoMapsTheNewOnes()
        {
            var vm = ExtendedSlot();
            var ps = Arrange(Deck, withDevice: true, ("RawBtn10", "Button 11"));

            vm.ProfileId = SC2026;

            var raw = Raw(ps);
            foreach (var target in new[] { "RawBtn18", "RawBtn19", "RawBtn20", "RawBtn21" })
                Assert.True(raw.ContainsKey(target),
                    target + " is a 2026 D-pad button and came up empty, so the profile "
                    + "change never ran the automap for the controls it added");
        }

        /// <summary>A launch restore stamps the incoming wire first, so the
        /// setter must do NOTHING: no move, and no refill of a target the
        /// user deliberately cleared.</summary>
        [Fact]
        public void ARestoreNeitherMovesNorRefills()
        {
            var vm = ExtendedSlot();
            var ps = Arrange(SC2026, withDevice: true, ("RawBtn10", "Button 11"));

            vm.ProfileId = SC2026;

            var raw = Raw(ps);
            Assert.Equal("Button 11", raw["RawBtn10"]);
            Assert.Single(raw);
        }

        /// <summary>An unlettered Extended profile has numbered rows and no
        /// role table. NintendoPreviewMap falls back to the Switch Pro table
        /// for an id it does not know, so translating one would move its
        /// bindings onto a wire that has nothing to do with it.</summary>
        [Fact]
        public void ANumberedProfileIsNeverTranslated()
        {
            var vm = ExtendedSlot();
            var ps = Arrange(HMaestroProfileCatalog.CustomProfileId, withDevice: false,
                ("RawBtn10", "Button 11"),
                ("RawBtn16", "Button 17"));

            vm.ProfileId = SC2026;

            var raw = Raw(ps);
            Assert.Equal("Button 11", raw["RawBtn10"]);
            Assert.Equal("Button 17", raw["RawBtn16"]);
        }
    }
}
