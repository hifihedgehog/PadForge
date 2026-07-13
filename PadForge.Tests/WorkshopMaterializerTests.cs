using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #9 Workshop import, App side: the materializer that turns the
    /// translator's neutral TranslatedProfile into a real ProfileData.
    /// The translator itself is covered by the golden suite in
    /// PadForge.SteamWorkshop.Tests; these tests pin the App-owned bag:
    /// slot topology arrays, MacroData/ActionData construction, and the
    /// XML persistence round-trip.
    /// </summary>
    public class WorkshopMaterializerTests
    {
        private static TranslatedProfile SampleProfile()
        {
            var t = new TranslatedProfile
            {
                Name = "Sample",
                Description = "d",
                NeedsXboxSlot = true,
                NeedsKbmSlot = true,
            };
            t.XboxMappingSet.Rows.Add(new MappingRow
            {
                Target = "ButtonA",
                Sources = { new MappingSource { Descriptor = "Gamepad Paddle2" } },
            });
            t.KbmMappingSet.Rows.Add(new MappingRow
            {
                Target = "KbmKey45",
                Sources = { new MappingSource { Descriptor = "Gamepad ButtonA" } },
            });
            t.KbmMappingSet.ShiftActivators.Add(new ShiftActivator
            {
                Descriptor = "Gamepad LeftShoulder",
                Mode = "Hold",
                LayerMask = "Layer_1_2",
                InheritUnmapped = true,
            });
            return t;
        }

        [Fact]
        public void Materialize_BuildsSplitConfigTopology()
        {
            var p = WorkshopProfileMaterializer.Materialize(SampleProfile());

            Assert.Equal("Sample", p.Name);
            Assert.Equal(16, p.SlotCreated.Length);
            Assert.True(p.SlotCreated[0]);
            Assert.True(p.SlotCreated[1]);
            Assert.DoesNotContain(true, p.SlotCreated.Skip(2));
            Assert.True(p.SlotEnabled[0]);
            Assert.True(p.SlotEnabled[1]);
            Assert.Equal((int)VirtualControllerType.Xbox, p.SlotControllerTypes[0]);
            Assert.Equal((int)VirtualControllerType.KeyboardMouse, p.SlotControllerTypes[1]);
            // HIDMaestro slug for the Xbox slot; KbM slots don't use
            // HIDMaestro and their documented default id is null
            // (InputManager.GetDefaultProfileId).
            Assert.False(string.IsNullOrEmpty(p.SlotProfileIds[0]));
            Assert.Null(p.SlotProfileIds[1]);

            Assert.Equal(16, p.SlotMappingSets.Length);
            Assert.Equal("Gamepad Paddle2", p.SlotMappingSets[0].Rows[0].Sources[0].Descriptor);
            Assert.Equal("KbmKey45", p.SlotMappingSets[1].Rows[0].Target);
            Assert.Single(p.SlotMappingSets[1].ShiftActivators);
            Assert.All(p.SlotMappingSets, s => Assert.NotNull(s));
        }

        [Fact]
        public void Materialize_BuildsAutofireMacro()
        {
            var t = SampleProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Autofire W (button_a)",
                Action = TranslatedMacroAction.RepeatKeyWhileHeld,
                TriggerMode = "WhileHeld",
                TriggerXboxButtons = Gamepad.A,
                ConsumeTrigger = true,
                VirtualKey = 0x57,
                IntervalMs = 99,
            });

            var p = WorkshopProfileMaterializer.Materialize(t);
            var m = Assert.Single(p.Macros);
            Assert.Equal(0, m.PadIndex); // macros ride the Xbox slot's output
            Assert.Equal(MacroTriggerSource.OutputController, m.TriggerSource);
            Assert.Equal(MacroTriggerMode.WhileHeld, m.TriggerMode);
            Assert.Equal(Gamepad.A, m.TriggerButtons);
            Assert.True(m.ConsumeTriggerButtons);
            var a = Assert.Single(m.Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.RepeatKeyWhileHeld, a.Type);
            Assert.Equal(0x57, a.KeyCode);
            Assert.Equal(99, a.IntervalMs);
        }

        [Fact]
        public void Materialize_BuildsCursorWarpMacro_PixelsWithinScreen()
        {
            var t = SampleProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Warp",
                Action = TranslatedMacroAction.MoveMouseToScreenPosition,
                TriggerMode = "OnPress",
                TriggerXboxButtons = Gamepad.Y,
                NormalizedX = 65535, // right edge
                NormalizedY = 0,     // top edge
            });

            var p = WorkshopProfileMaterializer.Materialize(t);
            var a = Assert.Single(Assert.Single(p.Macros).Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.MoveMouseToScreenPosition, a.Type);
            // The conversion targets the primary monitor; without pinning the
            // machine's resolution, the edges must land inside it.
            Assert.True(a.MouseX > 0, $"right edge should be positive, got {a.MouseX}");
            Assert.Equal(0, a.MouseY);
        }

        [Fact]
        public void Materialize_BuildsAxisTriggeredMacro()
        {
            var t = SampleProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Tap E (trigger click)",
                Action = TranslatedMacroAction.KeyTap,
                TriggerMode = "OnRelease",
                TriggerXboxButtons = 0,
                TriggerAxisTarget = "LeftTrigger",
                TriggerAxisThresholdPercent = 75,
                VirtualKey = 0x45,
            });

            var p = WorkshopProfileMaterializer.Materialize(t);
            var m = Assert.Single(p.Macros);
            Assert.Equal(MacroTriggerMode.OnRelease, m.TriggerMode);
            Assert.Equal(0, (int)m.TriggerButtons);
            Assert.Equal("LeftTrigger", m.TriggerAxisTargets);
            Assert.Equal(75, m.TriggerAxisThreshold);
            var a = Assert.Single(m.Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.KeyPress, a.Type);
            Assert.Equal(0x45, a.KeyCode);
        }

        [Fact]
        public void MaterializedProfile_RoundTripsThroughXml()
        {
            var t = SampleProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Autofire W",
                Action = TranslatedMacroAction.RepeatKeyWhileHeld,
                TriggerMode = "WhileHeld",
                TriggerXboxButtons = Gamepad.A,
                VirtualKey = 0x57,
                IntervalMs = 120,
            });
            var p = WorkshopProfileMaterializer.Materialize(t);

            var serializer = new XmlSerializer(typeof(PadForge.Services.ProfileData));
            using var buffer = new MemoryStream();
            serializer.Serialize(buffer, p);
            buffer.Position = 0;
            var clone = (PadForge.Services.ProfileData)serializer.Deserialize(buffer);

            Assert.Equal(p.Name, clone.Name);
            Assert.Equal(16, clone.SlotMappingSets.Length);
            Assert.Equal("ButtonA", clone.SlotMappingSets[0].Rows[0].Target);
            Assert.Equal("Gamepad Paddle2", clone.SlotMappingSets[0].Rows[0].Sources[0].Descriptor);
            Assert.Equal("KbmKey45", clone.SlotMappingSets[1].Rows[0].Target);
            var act = Assert.Single(clone.SlotMappingSets[1].ShiftActivators);
            Assert.Equal("Layer_1_2", act.LayerMask);
            Assert.True(act.InheritUnmapped);
            var macro = Assert.Single(clone.Macros);
            Assert.Equal(MacroTriggerSource.OutputController, macro.TriggerSource);
            Assert.Equal(Gamepad.A, macro.TriggerButtons);
            Assert.Equal(120, Assert.Single(macro.Actions).IntervalMs);
        }

        [Fact]
        public void Materialize_NullMacros_AndEmptySets_AreValid()
        {
            // An empty translation demands no slots at all.
            var p = WorkshopProfileMaterializer.Materialize(new TranslatedProfile { Name = "Empty" });
            Assert.Null(p.Macros);
            Assert.All(p.SlotMappingSets, s => Assert.NotNull(s));
            Assert.DoesNotContain(true, p.SlotCreated);
            Assert.DoesNotContain(true, p.SlotEnabled);
        }

        [Fact]
        public void Materialize_KeyboardOnlyConfig_CreatesOnlyTheKbmSlot()
        {
            // Owner report 2026-07-13: a pure keyboard config (Gubble)
            // imported with an empty Xbox VC alongside the mapped KbM slot.
            var t = new TranslatedProfile { Name = "KbOnly", NeedsKbmSlot = true };
            t.KbmMappingSet.Rows.Add(new MappingRow
            {
                Target = "KbmKey57",
                Sources = { new MappingSource { Descriptor = "Gamepad ButtonA" } },
            });

            var p = WorkshopProfileMaterializer.Materialize(t);
            Assert.True(p.SlotCreated[0]);
            Assert.DoesNotContain(true, p.SlotCreated.Skip(1));
            Assert.Equal((int)VirtualControllerType.KeyboardMouse, p.SlotControllerTypes[0]);
            Assert.Equal("KbmKey57", p.SlotMappingSets[0].Rows[0].Target);
        }

        [Fact]
        public void Materialize_PassthroughOnlyConfig_KeepsTheZeroRowXboxSlot()
        {
            // A default-automap passthrough carries zero explicit rows but
            // still needs the Xbox slot; the translator marks the demand.
            var t = new TranslatedProfile { Name = "Passthrough", NeedsXboxSlot = true };

            var p = WorkshopProfileMaterializer.Materialize(t);
            Assert.True(p.SlotCreated[0]);
            Assert.DoesNotContain(true, p.SlotCreated.Skip(1));
            Assert.Equal((int)VirtualControllerType.Xbox, p.SlotControllerTypes[0]);
            Assert.Empty(p.SlotMappingSets[0].Rows);
            Assert.False(string.IsNullOrEmpty(p.SlotProfileIds[0]));
        }
    }
}
