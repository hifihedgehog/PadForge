using System.Collections.Generic;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Models2D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The owner's reproduction, as a test: a DualSense already assigned to a
    /// Nintendo slot, the slot's profile then changed from Switch Pro to
    /// Switch 2 Pro. Minus, Home, Capture and the left stick click were
    /// reported as not carrying over.
    ///
    /// Those four are exactly the roles whose Switch 2 wire index is 14-17,
    /// i.e. past the end of the original's 14-button wire, which is the shape
    /// of the bug rather than a coincidence.
    /// </summary>
    public class Switch2ProAutomapRepro
    {
        private const string S1 = "switch-pro";
        private const string S2 = "switch2-pro-controller";

        private static DeviceObjectItem Obj(int idx, DeviceObjectTypeFlags kind) => new()
        {
            InputIndex = idx,
            ObjectType = kind,
        };

        /// <summary>A DualSense as PadForge sees it: six axes, buttons 0-11
        /// (through Misc1, the mic button), a hat, and no paddles.</summary>
        private static UserDevice DualSense()
        {
            var objs = new List<DeviceObjectItem>();
            for (int i = 0; i < 6; i++) objs.Add(Obj(i, DeviceObjectTypeFlags.AbsoluteAxis));
            for (int i = 0; i < 12; i++) objs.Add(Obj(i, DeviceObjectTypeFlags.PushButton));
            objs.Add(Obj(16, DeviceObjectTypeFlags.PushButton));   // touchpad click
            objs.Add(Obj(0, DeviceObjectTypeFlags.PointOfViewController));
            return new UserDevice
            {
                CapType = (int)InputDeviceType.Gamepad,
                DeviceObjects = objs.ToArray(),
            };
        }

        /// <summary>Automapping a DualSense straight onto a Switch 2 Pro slot
        /// binds all four, at their real Switch 2 indices.</summary>
        [Theory]
        [InlineData("ButtonBack", "Button 6")]        // Minus
        [InlineData("ButtonGuide", "Button 10")]      // Home
        [InlineData("ButtonShare", "Button 11")]      // Capture
        [InlineData("LeftThumbButton", "Button 8")]   // L3
        public void FreshAutomap_OntoSwitch2_BindsTheReportedFour(string role, string source)
        {
            var ps = SettingsManager.CreateDefaultPadSetting(
                DualSense(), VirtualControllerType.Nintendo, S2);

            int i = NintendoPreviewMap.IndexOf(S2, role);
            Assert.True(i >= 0, $"{role} missing from the Switch 2 wire");
            Assert.Equal(source, ps.GetRawMapping($"RawBtn{i}"));
        }

        /// <summary>And the whole surface: every role the DualSense can drive
        /// is bound, none left empty.</summary>
        [Fact]
        public void FreshAutomap_OntoSwitch2_LeavesNoDrivableRoleEmpty()
        {
            var ps = SettingsManager.CreateDefaultPadSetting(
                DualSense(), VirtualControllerType.Nintendo, S2);

            string[] drivable =
            {
                "ButtonB", "ButtonA", "ButtonY", "ButtonX",
                "LeftShoulder", "RightShoulder", "LeftTrigger", "RightTrigger",
                "ButtonBack", "ButtonStart", "LeftThumbButton", "RightThumbButton",
                "ButtonGuide", "ButtonShare",
                "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
            };
            foreach (var role in drivable)
            {
                int i = NintendoPreviewMap.IndexOf(S2, role);
                Assert.True(i >= 0, $"{role} missing from the Switch 2 wire");
                Assert.False(string.IsNullOrEmpty(ps.GetRawMapping($"RawBtn{i}")),
                    $"{role} (RawBtn{i}) left unmapped");
            }
        }

        /// <summary>The owner's actual path: the slot was already automapped
        /// on Switch Pro, then the profile changed. Every binding must move to
        /// the equivalent role, not stay on its old index.</summary>
        [Fact]
        public void ProfileChange_MovesEveryBindingToItsSwitch2Index()
        {
            var before = SettingsManager.CreateDefaultPadSetting(
                DualSense(), VirtualControllerType.Nintendo, S1);

            // What the four were bound to on the original wire.
            var expect = new Dictionary<string, string>();
            foreach (var role in new[] { "ButtonBack", "ButtonGuide", "ButtonShare", "LeftThumbButton" })
            {
                int i = NintendoPreviewMap.IndexOf(S1, role);
                string src = before.GetRawMapping($"RawBtn{i}");
                Assert.False(string.IsNullOrEmpty(src), $"{role} was not bound on {S1}");
                expect[role] = src;
            }

            // Translate the whole raw set the way the profile change does.
            var moved = new Dictionary<string, string>();
            foreach (var e in before.RawMappingEntries)
            {
                string dst = NintendoPreviewMap.TranslateRawTarget(e.Key, S1, S2);
                if (dst != null) moved[dst] = e.Value;
            }

            foreach (var kv in expect)
            {
                int i = NintendoPreviewMap.IndexOf(S2, kv.Key);
                Assert.True(moved.TryGetValue($"RawBtn{i}", out var got),
                    $"{kv.Key} did not land on RawBtn{i}");
                Assert.Equal(kv.Value, got);
            }
        }

        /// <summary>The grid reads MappingSet ROWS, not PadSetting raw
        /// entries, so a profile change has to move the rows too. Translating
        /// only the PadSetting left the four that land past the original's
        /// 14-button wire without any row at all, which is what rendered them
        /// empty, while the rows still keyed to the old indices kept their
        /// sources and silently named different buttons.</summary>
        [Fact]
        public void ProfileChange_MovesMappingSetRowsNotJustThePadSetting()
        {
            var ps = SettingsManager.CreateDefaultPadSetting(
                DualSense(), VirtualControllerType.Nintendo, S1);
            string guid = System.Guid.NewGuid().ToString();
            var ms = MappingSetMigrator.BuildFromLegacy(0,
                new (string, PadSetting, bool)[] { (guid, ps, true) });

            // Rows exist on the ORIGINAL wire's indices to begin with.
            foreach (var role in new[] { "ButtonBack", "ButtonGuide", "ButtonShare", "LeftThumbButton" })
            {
                int i = NintendoPreviewMap.IndexOf(S1, role);
                Assert.NotNull(ms.Rows.Find(r => r?.Target == $"RawBtn{i}"));
            }

            // Same transform the profile change applies to the row targets.
            var kept = new List<MappingRow>();
            foreach (var row in ms.Rows)
            {
                string dst = NintendoPreviewMap.TranslateRawTarget(row.Target, S1, S2);
                if (dst == null) continue;
                row.Target = dst;
                kept.Add(row);
            }
            ms.Rows.Clear();
            ms.Rows.AddRange(kept);

            // Now they exist on the SWITCH 2 indices, sources intact, and
            // nothing is left sitting on an index that means something else.
            foreach (var (role, source) in new[]
            {
                ("ButtonBack", "Button 6"), ("ButtonGuide", "Button 10"),
                ("ButtonShare", "Button 11"), ("LeftThumbButton", "Button 8"),
            })
            {
                int i = NintendoPreviewMap.IndexOf(S2, role);
                var row = ms.Rows.Find(r => r?.Target == $"RawBtn{i}");
                Assert.True(row != null, $"{role} has no row at RawBtn{i}");
                Assert.Equal(source, row.Sources?[0]?.Descriptor);
            }
            Assert.Single(ms.Rows.FindAll(r => r?.Target == "RawBtn14"));
        }
    }
}
