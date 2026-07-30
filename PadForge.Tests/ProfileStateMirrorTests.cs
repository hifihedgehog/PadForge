using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Locks the mirror contracts an audit (2026-07-14) found broken. Each
    /// mirror is one of several sites that must all carry the same state, and
    /// every failure here was a field silently dropped at exactly one of them.
    ///
    /// <para>Every test below was mutation-proved: with its fix reverted the
    /// test fails, so it locks the contract rather than restating the
    /// implementation. That check is the point. The whole family of
    /// NoInherit / Menus / slot-index bugs shipped under a green suite
    /// precisely because nothing asserted them.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ProfileStateMirrorTests : IDisposable
    {
        private static readonly Guid PadGuid = new("55555555-5555-5555-5555-555555555555");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly MappingSet[] _savedMappingSets;
        private readonly Action _savedAfterRefresh;

        public ProfileStateMirrorTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedMappingSets = SettingsManager.SlotMappingSets;
            _savedAfterRefresh = SettingsService.AfterMappingSetsRefreshed;
            SettingsService.AfterMappingSetsRefreshed = null;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsService.AfterMappingSetsRefreshed = _savedAfterRefresh;
        }

        private static void ArrangeEmptySlots()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
        }

        /// <summary>A NoInherit row as it actually ships: on a NON-Base layer.
        /// NoInherit is inert on Base (SettingsService forces it false there
        /// and the evaluator only consults it for a layer row), so asserting
        /// it on a Base row would test the one case that cannot fail.</summary>
        private static MappingRow ShiftLayerBlockRow() => new()
        {
            Target = "ButtonA",
            LayerMask = "Shift1",
            NoInherit = true,
        };

        // ── M1: NoInherit across the MappingRow copy sites ──

        [Fact]
        public void CloneMappingSetDeep_CarriesNoInherit()
        {
            var src = new MappingSet();
            src.Rows.Add(ShiftLayerBlockRow());

            var copy = InputService.CloneMappingSetDeep(src);

            var row = Assert.Single(copy.Rows);
            Assert.True(row.NoInherit,
                "Profile snapshot/apply dropped NoInherit: the layer stops blocking fallthrough after a profile round-trip.");
        }

        [Fact]
        public void ReplaceSlotMappingSet_CarriesNoInherit()
        {
            ArrangeEmptySlots();
            var src = new MappingSet();
            src.Rows.Add(ShiftLayerBlockRow());
            SettingsManager.SlotMappingSets[1] = src;

            InputService.ReplaceSlotMappingSet(targetSlot: 2, sourceSlot: 1);

            var row = Assert.Single(SettingsManager.SlotMappingSets[2].Rows);
            Assert.True(row.NoInherit, "Copy From Slot dropped NoInherit.");
        }

        [Fact]
        public void ApplySlotMappingSetFromRows_CarriesNoInherit()
        {
            ArrangeEmptySlots();

            InputService.ApplySlotMappingSetFromRows(3, new List<MappingRow> { ShiftLayerBlockRow() });

            var row = Assert.Single(SettingsManager.SlotMappingSets[3].Rows);
            Assert.True(row.NoInherit, "Paste-rows dropped NoInherit.");
        }

        [Fact]
        public void ExtractAllRowsForSlot_CarriesNoInherit()
        {
            // The Copy half of the Copy/Paste pair. Losing the flag here means
            // the clipboard snapshot is already wrong before Paste runs.
            ArrangeEmptySlots();
            var ms = new MappingSet();
            ms.Rows.Add(ShiftLayerBlockRow());
            SettingsManager.SlotMappingSets[0] = ms;

            var rows = InputService.ExtractAllRowsForSlot(0);

            Assert.True(Assert.Single(rows).NoInherit, "Copy-slot snapshot dropped NoInherit.");
        }

        [Fact]
        public void ExtractDeviceScopedRowsForSlot_CarriesNoInherit()
        {
            // The per-device Copy slice. Only rows carrying the device's own
            // sources survive, so the probe row needs one.
            ArrangeEmptySlots();
            var row = ShiftLayerBlockRow();
            row.Sources = new List<MappingSource>
            {
                new() { Descriptor = "Button 0", DeviceGuid = PadGuid.ToString().ToLowerInvariant() },
            };
            var ms = new MappingSet();
            ms.Rows.Add(row);
            SettingsManager.SlotMappingSets[0] = ms;

            var rows = InputService.ExtractDeviceScopedRowsForSlot(0, PadGuid);

            Assert.True(Assert.Single(rows).NoInherit, "Copy-from-device slice dropped NoInherit.");
        }

        [Fact]
        public void NoInherit_False_AlsoRoundTrips()
        {
            // Negative control: the assertions above would also pass if a copy
            // site hard-coded NoInherit = true.
            var src = new MappingSet();
            src.Rows.Add(new MappingRow { Target = "ButtonA", LayerMask = "Shift1", NoInherit = false });

            Assert.False(Assert.Single(InputService.CloneMappingSetDeep(src).Rows).NoInherit);
        }

        // ── M2: the legacy merge rebuilds the container, so every non-Rows
        //        field has to be carried across by hand ──

        [Fact]
        public void LegacyMerge_PreservesMenus()
        {
            ArrangeEmptySlots();
            var ms = new MappingSet();
            ms.Menus.Add(new MenuDefinitionEntry { Name = "Radial 1" });
            SettingsManager.SlotMappingSets[0] = ms;

            SettingsService.RefreshMappingSetsFromLegacy();

            var menu = Assert.Single(SettingsManager.SlotMappingSets[0].Menus);
            Assert.Equal("Radial 1", menu.Name);
        }

        [Fact]
        public void LegacyMerge_PreservesBaseLayerAppearance()
        {
            ArrangeEmptySlots();
            SettingsManager.SlotMappingSets[0] = new MappingSet
            {
                BaseLayerName = "Driving",
                BaseColor = "#FF8800",
                BaseIcon = "\U0001F3CE",
            };

            SettingsService.RefreshMappingSetsFromLegacy();

            var merged = SettingsManager.SlotMappingSets[0];
            Assert.Equal("Driving", merged.BaseLayerName);
            Assert.Equal("#FF8800", merged.BaseColor);
            Assert.Equal("\U0001F3CE", merged.BaseIcon);
        }

        // ── M3: menus are slot data ──

        [Fact]
        public void SlotHasAnyMapping_CountsAMenuOnlySlot()
        {
            ArrangeEmptySlots();
            var ms = new MappingSet();
            ms.Menus.Add(new MenuDefinitionEntry { Name = "Radial 1" });
            SettingsManager.SlotMappingSets[4] = ms;

            // ReplaceSlotMappingSet copies Menus, so a menus-only slot is a
            // real Copy From Slot donor and must be offered as one.
            Assert.True(InputService.SlotHasAnyMapping(4));
        }

        [Fact]
        public void SlotHasAnyMapping_StillFalseForAnEmptySet()
        {
            // Same-window negative control for the test above.
            ArrangeEmptySlots();
            SettingsManager.SlotMappingSets[4] = new MappingSet();
            Assert.False(InputService.SlotHasAnyMapping(4));
        }

        // ── M6 / M7 / M8: slot compaction ──

        /// <summary>Slot 3 is the only created slot, so compaction moves it to
        /// index 0. Everything keyed by slot index has to move with it.</summary>
        private static (ProfileData p, Dictionary<int, int> map) ArrangeGappyProfile()
        {
            var p = new ProfileData
            {
                SlotCreated = new bool[InputManager.MaxPads],
                SlotEnabled = new bool[InputManager.MaxPads],
                SlotMappingSets = new MappingSet[InputManager.MaxPads],
            };
            p.SlotCreated[3] = true;
            p.SlotEnabled[3] = true;

            var (map, needs) = InputService.BuildCompactionMap(p);
            Assert.True(needs);
            Assert.Equal(0, map[3]);
            return (p, map);
        }

        [Fact]
        public void Compaction_RemapsMacroPadIndex()
        {
            var (p, map) = ArrangeGappyProfile();
            p.Macros = new[] { new MacroData { PadIndex = 3, Name = "Rapid Fire" } };

            InputService.CompactProfileDataInPlace(p, map, InputManager.MaxPads);

            Assert.Equal(0, p.Macros[0].PadIndex);
        }

        [Fact]
        public void Compaction_RemapsDeviceSlotConfigSlotIndex()
        {
            var (p, map) = ArrangeGappyProfile();
            p.DeviceSlotConfigs = new[]
            {
                new PadForge.ViewModels.DeviceSlotConfigData { SlotIndex = 3, DeviceGuid = PadGuid },
            };

            InputService.CompactProfileDataInPlace(p, map, InputManager.MaxPads);

            Assert.Equal(0, p.DeviceSlotConfigs[0].SlotIndex);
        }

        [Fact]
        public void Compaction_LeavesLegacyNullMappingSetsNull()
        {
            // Null SlotMappingSets is the pre-multi-source sentinel meaning
            // "leave the live sets alone". Handing back a fresh all-null array
            // reads as "this profile has no mappings", and ApplyProfile then
            // clones null over every live slot, wiping the lot.
            var (p, map) = ArrangeGappyProfile();
            p.SlotMappingSets = null;

            InputService.CompactProfileDataInPlace(p, map, InputManager.MaxPads);

            Assert.Null(p.SlotMappingSets);
        }

        [Fact]
        public void Compaction_StillMovesRealMappingSets()
        {
            // Same-window positive control: the null guard above must not stop
            // a populated array from being compacted.
            var (p, map) = ArrangeGappyProfile();
            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow { Target = "ButtonA" });
            p.SlotMappingSets[3] = ms;

            InputService.CompactProfileDataInPlace(p, map, InputManager.MaxPads);

            Assert.Same(ms, p.SlotMappingSets[0]);
            Assert.Null(p.SlotMappingSets[3]);
        }

        // ── Audit 2026-07-17 S1-S4: Workshop stamps and activator
        //    direction stamps across the container-copy family ──

        private static MappingSet StampedWorkshopSet()
        {
            var ms = new MappingSet
            {
                Authoritative = true,
                WorkshopLeftStickDeadZoneShape = "0",
                WorkshopRightStickDeadZoneShape = "2",
                WorkshopGyroEngageDescriptor = "Button 9",
                WorkshopGyroEngageInvert = true,
            };
            var row = new MappingRow { Target = "ButtonA" };
            // Device-free source: survives the legacy merge's departed-device
            // sweep on a slot with no assigned devices.
            row.Sources.Add(new MappingSource { Descriptor = "Button 0" });
            ms.Rows.Add(row);
            return ms;
        }

        private static void AssertWorkshopStamps(MappingSet copy, string site)
        {
            Assert.True(copy.Authoritative, site + " dropped Authoritative.");
            Assert.Equal("0", copy.WorkshopLeftStickDeadZoneShape);
            Assert.Equal("2", copy.WorkshopRightStickDeadZoneShape);
            Assert.Equal("Button 9", copy.WorkshopGyroEngageDescriptor);
            Assert.True(copy.WorkshopGyroEngageInvert, site + " dropped WorkshopGyroEngageInvert.");
        }

        [Fact]
        public void CloneMappingSetDeep_CarriesWorkshopStamps()
        {
            // S1: profile snapshot AND apply both route here, so a dropped
            // stamp was dead on arrival and wiped from the live set on resave.
            AssertWorkshopStamps(
                InputService.CloneMappingSetDeep(StampedWorkshopSet()),
                "CloneMappingSetDeep");
        }

        [Fact]
        public void LegacyMerge_PreservesWorkshopStamps()
        {
            // S2: the merge rebuilds the container on every device assign /
            // unassign, so a dropped stamp died on the first topology change.
            ArrangeEmptySlots();
            SettingsManager.SlotMappingSets[0] = StampedWorkshopSet();

            SettingsService.RefreshMappingSetsFromLegacy();

            AssertWorkshopStamps(SettingsManager.SlotMappingSets[0], "MergeMappingSetsFromLegacy");
        }

        [Fact]
        public void ReplaceSlotMappingSet_CarriesWorkshopStamps()
        {
            // S3: Copy From Slot reproduces the source set wholesale.
            ArrangeEmptySlots();
            SettingsManager.SlotMappingSets[1] = StampedWorkshopSet();

            InputService.ReplaceSlotMappingSet(targetSlot: 2, sourceSlot: 1);

            AssertWorkshopStamps(SettingsManager.SlotMappingSets[2], "ReplaceSlotMappingSet");
        }

        [Fact]
        public void CloneMappingSetDeep_CarriesEveryShiftActivatorField()
        {
            // S4: CopyShiftActivators hand-listed fields and omitted the v15
            // AxisHalf / AxisInvert direction stamps. Probe EVERY public
            // read-write field with a non-default value so the next appended
            // field cannot silently drop from the copy family either.
            var probe = new ShiftActivator();
            var props = typeof(ShiftActivator).GetProperties()
                .Where(p => p.CanRead && p.CanWrite).ToArray();
            Assert.NotEmpty(props);
            int i = 0;
            foreach (var p in props)
            {
                object v =
                      p.PropertyType == typeof(string) ? "probe-" + p.Name
                    : p.PropertyType == typeof(bool) ? !(bool)p.GetValue(probe)
                    : p.PropertyType == typeof(int) ? (object)(1000 + i)
                    : p.PropertyType == typeof(double) ? (object)(0.125 * (i + 3))
                    : throw new InvalidOperationException(
                        "Unhandled ShiftActivator property type: " + p.PropertyType);
                p.SetValue(probe, v);
                i++;
            }
            // The retarget path is exercised by the ReplaceSlotMappingSet
            // tests. Here the plain deep clone must round-trip every field.
            var src = new MappingSet();
            src.ShiftActivators.Add(probe);

            var copy = Assert.Single(InputService.CloneMappingSetDeep(src).ShiftActivators);

            foreach (var p in props)
                Assert.True(Equals(p.GetValue(probe), p.GetValue(copy)),
                    $"CopyShiftActivators dropped ShiftActivator.{p.Name}.");
        }

        // ── Audit 2026-07-17 G5: sanitize dedup key carries GateDescriptor ──

        [Fact]
        public void Sanitize_KeepsGatedAndUngatedTwinsApart()
        {
            var ms = new MappingSet();
            var row = new MappingRow { Target = "DPadUp" };
            row.Sources.Add(new MappingSource { Descriptor = "Touchpad 0 Finger 0 Down Upper" });
            row.Sources.Add(new MappingSource
            {
                Descriptor = "Touchpad 0 Finger 0 Down Upper",
                GateDescriptor = "Touchpad 0 Click",
            });
            // Same-window control: a TRUE duplicate must still collapse.
            row.Sources.Add(new MappingSource { Descriptor = "Touchpad 0 Finger 0 Down Upper" });
            ms.Rows.Add(row);

            SettingsService.SanitizeMappingSet(ms, 0);

            Assert.Equal(2, Assert.Single(ms.Rows).Sources.Count);
        }

        // ── M10 sibling: null vs empty Macros ──

        [Fact]
        public void ProfileMacros_EmptyAndNull_AreDistinctThroughXml()
        {
            // The whole null-vs-empty contract rests on this surviving
            // serialization: null omits the element, empty writes it.
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(ProfileData));

            string Write(MacroData[] macros)
            {
                var p = new ProfileData { Macros = macros };
                using var sw = new System.IO.StringWriter();
                serializer.Serialize(sw, p);
                return sw.ToString();
            }

            ProfileData Read(string xml)
            {
                using var sr = new System.IO.StringReader(xml);
                return (ProfileData)serializer.Deserialize(sr);
            }

            Assert.Null(Read(Write(null)).Macros);

            var empty = Read(Write(Array.Empty<MacroData>())).Macros;
            Assert.NotNull(empty);
            Assert.Empty(empty);
        }
    }
}
