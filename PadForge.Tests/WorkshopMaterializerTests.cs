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
            // Continuous actions only stop on release via UntilRelease
            // (Step4b's stop clause); Once would autofire forever after
            // the trigger released (wave 2A fix).
            Assert.Equal(MacroRepeatMode.UntilRelease, m.RepeatMode);
            var a = Assert.Single(m.Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.RepeatKeyWhileHeld, a.Type);
            Assert.Equal(0x57, a.KeyCode);
            Assert.Equal(99, a.IntervalMs);
        }

        // ─── Wave 2A lowerings ──────────────────────────────────────────

        [Fact]
        public void Materialize_BuildsVcTurboMacro_UntilRelease()
        {
            var t = SampleProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Turbo ButtonB (button_a)",
                Action = TranslatedMacroAction.RepeatVcButtonWhileHeld,
                TriggerMode = "WhileHeld",
                TriggerXboxButtons = Gamepad.A,
                TargetXboxButtons = Gamepad.B,
                IntervalMs = 125,
            });

            var m = Assert.Single(WorkshopProfileMaterializer.Materialize(t).Macros);
            Assert.Equal(MacroTriggerMode.WhileHeld, m.TriggerMode);
            Assert.Equal(MacroRepeatMode.UntilRelease, m.RepeatMode);
            var a = Assert.Single(m.Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.RepeatVcButtonWhileHeld, a.Type);
            Assert.Equal(Gamepad.B, a.ButtonFlags);
            Assert.Equal(125, a.IntervalMs);
        }

        [Fact]
        public void Materialize_BuildsHoldVcButton_AsHoldForMsButtonPress()
        {
            var t = SampleProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Long press ButtonY (button_a)",
                Action = TranslatedMacroAction.HoldVcButton,
                TriggerMode = "HoldForMs",
                TriggerHoldMs = 300,
                TriggerXboxButtons = Gamepad.A,
                TargetXboxButtons = Gamepad.Y,
                ConsumeTrigger = true,
            });

            var m = Assert.Single(WorkshopProfileMaterializer.Materialize(t).Macros);
            Assert.Equal(MacroTriggerMode.HoldForMs, m.TriggerMode);
            Assert.Equal(300, m.TriggerHoldMs);
            // The hold shape: restart the one-action sequence every frame
            // (RepeatDelayMs 0) until the physical release stops it.
            Assert.Equal(MacroRepeatMode.UntilRelease, m.RepeatMode);
            Assert.Equal(0, m.RepeatDelayMs);
            Assert.True(m.ConsumeTriggerButtons);
            var a = Assert.Single(m.Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.ButtonPress, a.Type);
            Assert.Equal(Gamepad.Y, a.ButtonFlags);
        }

        [Fact]
        public void Materialize_HoldForMs_ClampsToVmRange()
        {
            var t = SampleProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Long press",
                Action = TranslatedMacroAction.HoldVcButton,
                TriggerMode = "HoldForMs",
                TriggerHoldMs = 10, // corpus can carry tiny values; VM floor is 50
                TriggerXboxButtons = Gamepad.A,
                TargetXboxButtons = Gamepad.Y,
            });

            var m = Assert.Single(WorkshopProfileMaterializer.Materialize(t).Macros);
            Assert.Equal(50, m.TriggerHoldMs);
        }

        [Fact]
        public void Materialize_BuildsToggleVcButton()
        {
            var t = SampleProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Toggle ButtonB (click)",
                Action = TranslatedMacroAction.ToggleVcButton,
                TriggerMode = "OnPress",
                TriggerXboxButtons = Gamepad.B,
                TargetXboxButtons = Gamepad.B,
            });

            var m = Assert.Single(WorkshopProfileMaterializer.Materialize(t).Macros);
            Assert.Equal(MacroTriggerMode.OnPress, m.TriggerMode);
            Assert.Equal(MacroRepeatMode.Once, m.RepeatMode);
            var a = Assert.Single(m.Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.ToggleVcButton, a.Type);
            Assert.Equal(Gamepad.B, a.ButtonFlags);
        }

        [Fact]
        public void Materialize_BuildsToggleKey_AndGyroRecenter()
        {
            var t = SampleProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Toggle LEFT_SHIFT (button_x)",
                Action = TranslatedMacroAction.ToggleKey,
                TriggerMode = "OnPress",
                TriggerXboxButtons = Gamepad.X,
                VirtualKey = 0xA0,
            });
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Recenter gyro (click)",
                Action = TranslatedMacroAction.GyroRecenter,
                TriggerMode = "OnPress",
                TriggerXboxButtons = Gamepad.RIGHT_THUMB,
            });

            var p = WorkshopProfileMaterializer.Materialize(t);
            Assert.Equal(2, p.Macros.Length);
            var key = Assert.Single(p.Macros[0].Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.ToggleKey, key.Type);
            Assert.Equal(0xA0, key.KeyCode);
            var gyro = Assert.Single(p.Macros[1].Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.GyroRecenter, gyro.Type);
        }

        [Fact]
        public void Materialize_MouseRegion_LowersToOnPressOnReleaseClampPair()
        {
            var t = SampleProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Cursor region (left_trigger)",
                Action = TranslatedMacroAction.MouseLimitRegion,
                TriggerMode = "WhileHeld", // semantic; lowered to the pair
                TriggerAxisTarget = "LeftTrigger",
                TriggerAxisThresholdPercent = 75,
                RegionXPercent = 25,
                RegionYPercent = 75,
                RegionScalePercent = 40,
            });

            var p = WorkshopProfileMaterializer.Materialize(t);
            Assert.Equal(2, p.Macros.Length);
            var engage = p.Macros[0];
            var release = p.Macros[1];
            Assert.Equal(MacroTriggerMode.OnPress, engage.TriggerMode);
            Assert.Equal(MacroTriggerMode.OnRelease, release.TriggerMode);
            Assert.Equal("LeftTrigger", engage.TriggerAxisTargets);
            Assert.Equal("LeftTrigger", release.TriggerAxisTargets);
            Assert.Equal(75, engage.TriggerAxisThreshold);

            foreach (var m in p.Macros)
            {
                var a = Assert.Single(m.Actions);
                Assert.Equal(PadForge.ViewModels.MacroActionType.MouseLimitRegion, a.Type);
                Assert.Equal(PadForge.ViewModels.CursorClampMode.XAndY, a.CursorClampMode);
                // A 40% region leaves (100-40)/2 = 30% inset per edge.
                if (GetPrimary(out int w, out int h))
                {
                    Assert.Equal((int)Math.Round(w * 0.30), a.CursorClampInsetX);
                    Assert.Equal((int)Math.Round(h * 0.30), a.CursorClampInsetY);
                }
            }
        }

        [Fact]
        public void Materialize_MouseRegion_FullScreenScale_HasZeroInsets()
        {
            var t = SampleProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Cursor region",
                Action = TranslatedMacroAction.MouseLimitRegion,
                TriggerMode = "WhileHeld",
                TriggerXboxButtons = Gamepad.A,
                RegionScalePercent = 100,
            });

            var p = WorkshopProfileMaterializer.Materialize(t);
            foreach (var m in p.Macros)
            {
                var a = Assert.Single(m.Actions);
                Assert.Equal(0, a.CursorClampInsetX);
                Assert.Equal(0, a.CursorClampInsetY);
            }
        }

        // The same metric source the materializer converts with, so the
        // assertion is DPI-context-proof in the test host.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private static bool GetPrimary(out int w, out int h)
        {
            w = GetSystemMetrics(0); // SM_CXSCREEN
            h = GetSystemMetrics(1); // SM_CYSCREEN
            return w > 0 && h > 0;
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

        // ─── set_led macros (Wave 1a, B-7) ──────────────────────────────

        private static TranslatedMacro SetLedMacro(int r, int g, int b,
            int brightness = 100, int saturation = 100, int setting = 1) => new()
        {
            Name = "Set LED (button_a)",
            Action = TranslatedMacroAction.SetLightbarColor,
            TriggerMode = "OnPress",
            TriggerXboxButtons = Gamepad.A,
            LedR = r,
            LedG = g,
            LedB = b,
            LedBrightnessPercent = brightness,
            LedSaturationPercent = saturation,
            LedSetting = setting,
        };

        [Fact]
        public void Materialize_SetLed_SettingOne_BuildsStickyLightbarHold()
        {
            var t = SampleProfile();
            t.Macros.Add(SetLedMacro(255, 0, 0, brightness: 43));

            var p = WorkshopProfileMaterializer.Materialize(t);
            var m = Assert.Single(p.Macros);
            Assert.Equal(MacroTriggerSource.OutputController, m.TriggerSource);
            Assert.Equal(Gamepad.A, m.TriggerButtons);
            var a = Assert.Single(m.Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.LightbarColor, a.Type);
            Assert.Equal(PadForge.ViewModels.MacroLightbarHoldMode.Sticky, a.LightbarHoldMode);
            Assert.Equal(PadForge.ViewModels.MacroLightbarColorSource.Fixed, a.LightbarColorSource);
            // Brightness 43% pre-scales V: (255,0,0) -> (110,0,0).
            Assert.Equal(110, a.LightbarR);
            Assert.Equal(0, a.LightbarG);
            Assert.Equal(0, a.LightbarB);
        }

        [Fact]
        public void Materialize_SetLed_SaturationFoldsViaHsv()
        {
            var t = SampleProfile();
            t.Macros.Add(SetLedMacro(255, 0, 0, brightness: 100, saturation: 50));

            var a = Assert.Single(Assert.Single(
                WorkshopProfileMaterializer.Materialize(t).Macros).Actions);
            // Half saturation of pure red: V stays 1, S 0.5 -> (255,128,128).
            Assert.Equal(255, a.LightbarR);
            Assert.Equal(128, a.LightbarG);
            Assert.Equal(128, a.LightbarB);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)] // "restore default": approximated as a clear
        public void Materialize_SetLed_SettingZeroAndTwo_BuildClear(int setting)
        {
            var t = SampleProfile();
            t.Macros.Add(SetLedMacro(255, 0, 0, setting: setting));

            var a = Assert.Single(Assert.Single(
                WorkshopProfileMaterializer.Materialize(t).Macros).Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.LightbarColorClear, a.Type);
        }

        [Theory]
        [InlineData("controller_steamcontroller_gordon")]
        [InlineData("controller_steamcontroller_headcrab")]
        public void Materialize_SetLed_SteamControllerFamily_DrivesGuideLed(string type)
        {
            var t = SampleProfile();
            t.Report.ControllerType = type;
            t.Macros.Add(SetLedMacro(255, 0, 0, brightness: 43));

            var a = Assert.Single(Assert.Single(
                WorkshopProfileMaterializer.Materialize(t).Macros).Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.GuideLedBrightness, a.Type);
            Assert.Equal(43, a.GuideLedPercent);
        }

        [Fact]
        public void Materialize_SetLed_RoundTripsThroughXml()
        {
            var t = SampleProfile();
            t.Macros.Add(SetLedMacro(217, 255, 0, brightness: 100, saturation: 100));
            var p = WorkshopProfileMaterializer.Materialize(t);

            var serializer = new XmlSerializer(typeof(PadForge.Services.ProfileData));
            using var buffer = new MemoryStream();
            serializer.Serialize(buffer, p);
            buffer.Position = 0;
            var clone = (PadForge.Services.ProfileData)serializer.Deserialize(buffer);

            var a = Assert.Single(Assert.Single(clone.Macros).Actions);
            Assert.Equal(PadForge.ViewModels.MacroActionType.LightbarColor, a.Type);
            Assert.Equal(PadForge.ViewModels.MacroLightbarHoldMode.Sticky, a.LightbarHoldMode);
            Assert.Equal(217, a.LightbarR);
            Assert.Equal(255, a.LightbarG);
            Assert.Equal(0, a.LightbarB);
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
