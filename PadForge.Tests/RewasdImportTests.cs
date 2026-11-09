using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.ConfigImport;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>#561: every row of the reWASD table, against hand-written
    /// files in the shape the published schema and the public samples use.</summary>
    [Collection("SettingsManagerStatics")]
    public class RewasdImportTests
    {
        private static readonly string AllKeyboards = SdlKeyboardWrapper.BuildGuid("aggregate://keyboards").ToString();
        private static readonly string AllMice = SdlKeyboardWrapper.BuildGuid("aggregate://mice").ToString();

        private const string PadAndKbm =
            """[{"id":1,"name":"gamepad","convertToVirtual":true},{"id":11,"name":"keyboard"},{"id":23,"name":"mouse"}]""";

        internal static string File(string masks = "[]", string mappings = "[]", string shifts = "[]",
            string commands = "[]", string hardware = PadAndKbm, string virtualType = "360", string extra = "",
            string config = """{"appName":"Test Game","processNames":["game.exe","launcher.exe"]}""")
            => "{\"schemaVersion\":\"3.0\",\"appVersion\":\"9.3.2\",\"config\":" + config
             + ",\"devices\":{\"hardware\":" + hardware + ",\"virtual\":{\"gamepads\":[{\"id\":1,\"type\":\"" + virtualType + "\"}]}}"
             + ",\"shifts\":" + shifts + ",\"masks\":" + masks + ",\"commands\":" + commands
             + ",\"mappings\":" + mappings + extra + "}";

        internal static string Mask(int id, int device, int button)
            => "{\"id\":" + id + ",\"set\":[{\"deviceId\":" + device + ",\"buttonId\":" + button + "}]}";

        internal static string Map(int mask, string steps, string type = "single", string mode = "hold_until_release",
            int shift = 0, string more = "", string activatorParams = "")
            => "{\"condition\":{" + (shift > 0 ? "\"shiftId\":" + shift + "," : "")
             + "\"mask\":{\"id\":" + mask + ",\"activator\":{\"type\":\"" + type + "\",\"mode\":\"" + mode + "\""
             + (activatorParams.Length > 0 ? ",\"params\":" + activatorParams : "") + "}}}"
             + (steps.Length > 0 ? ",\"macros\":" + steps : "") + more + "}";

        private static string Key(int scan, string action = null)
            => "{\"keyboard\":{\"buttonId\":" + scan + (action != null ? ",\"action\":\"" + action + "\"" : "") + "}}";

        internal static ForeignImportResult Import(string json, string path = "C:/profiles/Test.rewasd")
            => ForeignConfigImporter.Translate(Encoding.UTF8.GetBytes(json), path);

        private static ProfileData Profile(string json)
        {
            var result = Import(json);
            Assert.False(result.Refused, result.RefusedKey);
            return result.Profile;
        }

        private static MappingRow Row(ProfileData p, string target, string layer = "Base")
            => p.SlotMappingSets.SelectMany(s => s.Rows).FirstOrDefault(r => r.Target == target && r.LayerMask == layer);

        private static MacroData Macro(ProfileData p, string name)
            => p.Macros.First(m => m.Name == name);

        private static ActionData Only(MacroData m) => Assert.Single(m.Actions);

        private static CustomInputState Centered()
        {
            var state = new CustomInputState();
            for (int i = 0; i < state.Axis.Length; i++) state.Axis[i] = 32768;
            return state;
        }

        // A stick pushed this percent of the way up or right.
        private static int Up(int percent) => 32768 - 32767 * percent / 100;
        private static int Right(int percent) => 32768 + 32767 * percent / 100;

        // A row's read of one source, its gate included.
        private static bool ButtonRead(CustomInputState state, MappingSource src)
            => SourceEvaluator.EvaluateForButtonTarget(state, src, 50, 0, "KbmKey20", 0, null, 0, "");

        // ── Which slots a file makes ────────────────────────────────────

        [Theory]
        [InlineData("360", VirtualControllerType.Xbox, "xbox-360-wired")]
        [InlineData("xboxonebt", VirtualControllerType.Xbox, "xbox-one-s-bt")]
        [InlineData("ds4", VirtualControllerType.PlayStation, "dualshock-4-v2")]
        [InlineData("ds3", VirtualControllerType.PlayStation, "dualshock-3")]
        [InlineData("nintendoswitchpro", VirtualControllerType.Nintendo, "switch-pro")]
        public void VirtualTypes_MakeTheirSlot(string type, VirtualControllerType expected, string profileId)
        {
            var p = Profile(File(virtualType: type));
            Assert.True(p.SlotCreated[0]);
            Assert.Equal((int)expected, p.SlotControllerTypes[0]);
            Assert.Equal(profileId, p.SlotProfileIds[0]);
            Assert.NotNull(HMaestroProfileCatalog.GetProfileById(profileId));
            Assert.True(p.SlotMappingSets[0].Authoritative);
            Assert.False(p.SlotMappingSets[1].Authoritative);
        }

        [Fact]
        public void KeyboardAndMouseVirtualType_MakesNoPadSlot()
        {
            var p = Profile(File(virtualType: "keyboardandmouse",
                masks: "[" + Mask(1, 11, 30) + "]", mappings: "[" + Map(1, "[" + Key(31) + "]") + "]"));
            Assert.Equal((int)VirtualControllerType.KeyboardMouse, p.SlotControllerTypes[0]);
            Assert.False(p.SlotCreated[1]);
        }

        [Fact]
        public void AVirtualGamepadNothingUses_MakesNoSlot()
        {
            // The file lists a virtual 360 either way. A gamepad that only
            // sends keys leaves the pad to the game.
            var p = Profile(File(hardware: """[{"id":1,"name":"gamepad"}]""",
                masks: "[" + Mask(1, 1, 1) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.Equal((int)VirtualControllerType.KeyboardMouse, p.SlotControllerTypes[0]);
            Assert.False(p.SlotCreated[1]);
            Assert.Null(Row(p, "ButtonA"));
            Assert.Equal("Gamepad ButtonA", Row(p, "KbmKey20").Sources[0].Descriptor);
        }

        [Fact]
        public void NameAndExecutables_ComeFromConfig()
        {
            var p = Profile(File());
            Assert.Equal("Test Game", p.Name);
            Assert.Equal("game.exe|launcher.exe", p.ExecutableNames);
        }

        [Fact]
        public void NoAppName_TakesTheGameFolderAndFile()
        {
            var r = Import(File(config: "{}"), "C:/Users/Public/Documents/reWASD/Profiles/Doom/Controller/Config 1.rewasd");
            Assert.Equal("Doom - Config 1", r.Profile.Name);
        }

        // ── Pass-through and hardware mappings ──────────────────────────

        [Fact]
        public void PhysicalPad_PassesThroughUntilUnmapped()
        {
            var p = Profile(File());
            Assert.Equal("Gamepad ButtonA", Row(p, "ButtonA").Sources[0].Descriptor);
            Assert.Equal("Gamepad LeftTrigger", Row(p, "LeftTrigger").Sources[0].Descriptor);
            Assert.Equal("Gamepad RightStickY", Row(p, "RightThumbAxisY").Sources[0].Descriptor);
            Assert.Equal("Gamepad DPadLeft", Row(p, "DPadLeft").Sources[0].Descriptor);
        }

        [Fact]
        public void AMaskMapping_LeavesTheNativeFunctionOn()
        {
            // The double input reWASD's own blog describes: the file has to
            // say unmap, or the button keeps its native output too.
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.NotNull(Row(p, "ButtonA"));
            Assert.NotNull(Row(p, "KbmKey20"));
        }

        [Fact]
        public void HardwareUnmap_RemovesTheRow()
        {
            var p = Profile(File(mappings: """[{"condition":{"hardware":{"deviceId":1,"buttonId":1}},"hardware":{"unmap":true}}]"""));
            Assert.Null(Row(p, "ButtonA"));
            Assert.NotNull(Row(p, "ButtonB"));
        }

        [Fact]
        public void HardwareRemap_MovesTheRow()
        {
            var p = Profile(File(mappings:
                """[{"condition":{"hardware":{"deviceId":1,"buttonId":1}},"hardware":{"buttonId":2}},{"condition":{"hardware":{"deviceId":1,"buttonId":2}},"hardware":{"buttonId":1}}]"""));
            Assert.Equal("Gamepad ButtonB", Row(p, "ButtonA").Sources[0].Descriptor);
            Assert.Equal("Gamepad ButtonA", Row(p, "ButtonB").Sources[0].Descriptor);
        }

        [Fact]
        public void HardwareStickRemap_CarriesInversion()
        {
            var p = Profile(File(mappings:
                """[{"condition":{"hardware":{"deviceId":1,"stick":"left","axis":"x"}},"hardware":{"stick":"right","axis":"y","inverted":true}}]"""));
            Assert.DoesNotContain(Row(p, "LeftThumbAxisX")?.Sources ?? new List<MappingSource>(), s => s.Descriptor == "Gamepad LeftStickX");
            var src = Row(p, "RightThumbAxisY").Sources.First(s => s.Descriptor == "Gamepad LeftStickX");
            Assert.True(src.Invert);
        }

        [Fact]
        public void HardwareTriggerUnmap_RemovesTheTrigger()
        {
            var p = Profile(File(mappings: """[{"condition":{"hardware":{"deviceId":1,"trigger":"left"}},"hardware":{"unmap":true}}]"""));
            Assert.Null(Row(p, "LeftTrigger"));
            Assert.NotNull(Row(p, "RightTrigger"));
        }

        [Fact]
        public void UnmappingOneStickDirection_LeavesTheOtherHalfWorking()
        {
            var state = new CustomInputState();
            var p = Profile(File(mappings: """[{"condition":{"hardware":{"deviceId":1,"buttonId":40}},"hardware":{"unmap":true}}]"""));
            var src = Assert.Single(Row(p, "LeftThumbAxisY").Sources);
            Assert.True(src.HalfAxis);

            // Y is +down in the row frame: pushed up now reads nothing, and
            // pushed down still reads down.
            state.Axis[1] = 0;
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(state, src, 0, false, ""), 2);
            state.Axis[1] = 65535;
            Assert.True(SourceCoercion.EvaluateForBipolarAxisTarget(state, src, 0, false, "") > 0.9f);
        }

        [Fact]
        public void UnmappedBothHalves_RemovesTheAxis()
        {
            var p = Profile(File(mappings:
                """[{"condition":{"hardware":{"deviceId":1,"buttonId":40}},"hardware":{"unmap":true}},{"condition":{"hardware":{"deviceId":1,"buttonId":41}},"hardware":{"unmap":true}}]"""));
            Assert.Null(Row(p, "LeftThumbAxisY"));
            Assert.NotNull(Row(p, "LeftThumbAxisX"));
        }

        [Fact]
        public void KeyboardUnmap_NamesConsumeMappedInputs()
        {
            var r = Import(File(mappings: """[{"condition":{"hardware":{"deviceId":11,"buttonId":30,"description":"DIK_A"}},"hardware":{"unmap":true}}]"""));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.ConsumeKeys);
        }

        [Fact]
        public void AnUnmappedKeyNoRowReads_TakesABlockTheHookHoldsBack()
        {
            // No row reads this key, so Consume Mapped Inputs would pass it.
            // An empty macro that consumes its trigger is what the hook eats.
            var r = Import(File(mappings: """[{"condition":{"hardware":{"deviceId":11,"buttonId":30,"description":"DIK_A"}},"hardware":{"unmap":true}}]"""));
            var md = Assert.Single(r.Profile.Macros);
            Assert.StartsWith("Block ", md.Name);
            Assert.True(md.ConsumeTriggerButtons);
            Assert.Empty(md.Actions);
            var item = SettingsService.LoadMacroFromData(md, VirtualControllerType.Xbox, null);
            Assert.True(InputService.MacroTriggerConsumesKey(item, Guid.Parse(ImportBuilder.AllKeyboardsGuid), out int vk));
            Assert.Equal(0x41, vk);
            // The keyboard lands on the macro's slot, where the hook looks.
            Assert.Contains(r.Profile.Entries, e => e.InstanceGuid == Guid.Parse(ImportBuilder.AllKeyboardsGuid) && e.MapTo == md.PadIndex);
        }

        [Fact]
        public void AnUnmappedKeyARowReads_TakesNoBlock()
        {
            // The row's read is held back already, and a block would stop the
            // row while the key is down.
            var r = Import(File(masks: "[" + Mask(1, 11, 30) + "]", mappings: "[" + Map(1, "[{\"gamepad\":{\"id\":1,\"buttonId\":1}}]") + ","
                + """{"condition":{"hardware":{"deviceId":11,"buttonId":30,"description":"DIK_A"}},"hardware":{"unmap":true}}]"""));
            Assert.NotNull(Row(r.Profile, "ButtonA"));
            Assert.DoesNotContain(r.Profile.Macros, m => m.ConsumeTriggerButtons);
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.ConsumeKeys);
        }

        /// <summary>reWASD sends a mapped key's native key unless the file
        /// unmaps it ("we suggest using Unmaps for keyboard keys, so the
        /// native action did not bother the mapping you have added", reWASD
        /// blog, How to reassign keyboard keys). The import asks for Consume
        /// Mapped Inputs on the keyboard because the file unmaps DIK_B, and
        /// that switch also holds back DIK_A, which a row reads with no
        /// unmap. That key gets its own line. DIK_C, mapped and unmapped,
        /// and DIK_D, remapped in hardware, which replaces its native key,
        /// get none.</summary>
        [Fact]
        public void AKeyMappedWithoutAnUnmap_SaysTheSwitchHoldsItBack()
        {
            var r = Import(File(
                masks: "[" + Mask(1, 11, 30) + "," + Mask(2, 11, 46) + "]",
                mappings: "[" + Map(1, PadOut(1)) + "," + Map(2, PadOut(3)) + ","
                    + """{"condition":{"hardware":{"deviceId":11,"buttonId":48,"description":"DIK_B"}},"hardware":{"unmap":true}},"""
                    + """{"condition":{"hardware":{"deviceId":11,"buttonId":46,"description":"DIK_C"}},"hardware":{"unmap":true}},"""
                    + """{"condition":{"hardware":{"deviceId":11,"buttonId":32,"description":"DIK_D"}},"hardware":{"buttonId":33}}]"""));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.ConsumeKeys);
            var line = Assert.Single(r.Report.Entries, e => e.ReasonKey == "Import_Tr_MappedKeyHeldBack");
            Assert.Equal(TranslationStatus.Partial, line.Status);
            Assert.Equal("Keyboard A", Assert.Single(line.ReasonArgs));
        }

        /// <summary>A hardware remap sits in reWASD's hardware layer beside
        /// the unmaps and replaces the native key, which the import models
        /// (replacesNative). So a keyboard or mouse whose only change is a
        /// hardware remap still gets the Consume Mapped Inputs advice, or the
        /// remapped key would type beside its new one. The remapped key gets
        /// no held-back line.</summary>
        [Fact]
        public void AHardwareRemapAlone_AsksForConsumeMappedInputs()
        {
            var r = Import(File(mappings: "["
                + """{"condition":{"hardware":{"deviceId":11,"buttonId":32,"description":"DIK_D"}},"hardware":{"buttonId":33}},"""
                + """{"condition":{"hardware":{"deviceId":23,"buttonId":4}},"hardware":{"buttonId":5}}]"""));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.ConsumeKeys && e.SourcePath == "Keyboard");
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.ConsumeKeys && e.SourcePath == "Mouse");
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.MappedKeyHeldBack);
        }

        /// <summary>With no unmap on the keyboard the import asks for no
        /// switch, and a mapped key reaches Windows in both programs.</summary>
        [Fact]
        public void AKeyMappedWithoutAnUnmap_HasNoLine_WhenTheFileUnmapsNoKey()
        {
            var r = Import(File(masks: "[" + Mask(1, 11, 30) + "]", mappings: "[" + Map(1, PadOut(1)) + "]"));
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == "Import_Tr_MappedKeyHeldBack");
        }

        [Fact]
        public void UnmappedMouseMovement_HasItsOwnLine()
        {
            // The hook holds back keys and mouse buttons, never movement.
            var r = Import(File(mappings: """[{"condition":{"hardware":{"deviceId":23,"stick":"mouse","axis":"x"}},"hardware":{"unmap":true}}]"""));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.MouseMovement);
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.ConsumeKeys);
            Assert.DoesNotContain(r.Profile.Macros, m => m.ConsumeTriggerButtons);
        }

        [Fact]
        public void KeyboardRemap_IsAKeyRow()
        {
            var p = Profile(File(mappings: """[{"condition":{"hardware":{"deviceId":11,"buttonId":30}},"hardware":{"buttonId":31}}]"""));
            var src = Assert.Single(Row(p, "KbmKey53").Sources); // S
            Assert.Equal("Button 65", src.Descriptor);          // A
            Assert.Equal(AllKeyboards, src.DeviceGuid);
        }

        [Fact]
        public void TwoHardwarePads_SayTheyShareTheSlot_OnlyWhenTheMappingsTellThemApart()
        {
            const string TwoPads = """[{"id":1,"name":"gamepad","convertToVirtual":true},{"id":2,"name":"gamepad","convertToVirtual":true}]""";
            var apart = Import(File(hardware: TwoPads,
                masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 2, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]") + "," + Map(2, "[" + Key(28) + "]") + "]"));
            Assert.Contains(apart.Report.Entries, e => e.ReasonKey == ImportReasons.SharedControllers);

            var same = Import(File(hardware: TwoPads, masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.DoesNotContain(same.Report.Entries, e => e.ReasonKey == ImportReasons.SharedControllers);
        }

        // ── Inputs ──────────────────────────────────────────────────────

        [Fact]
        public void KeyboardInput_NamesAllKeyboards_NeverAnyDevice()
        {
            // Button 9 with an empty device guid also reads a gamepad's right
            // stick click, and Tab is Button 9.
            var p = Profile(File(masks: "[" + Mask(1, 11, 15) + "]",
                mappings: "[" + Map(1, """[{"gamepad":{"id":1,"buttonId":1}}]""") + "]"));
            var src = Row(p, "ButtonA").Sources.First(s => s.Descriptor == "Button 9");
            Assert.Equal(AllKeyboards, src.DeviceGuid);
        }

        [Fact]
        public void MouseInput_NamesAllMice_InTheMouseStateOrder()
        {
            var p = Profile(File(masks: "[" + Mask(1, 23, 2) + "," + Mask(2, 23, 3) + "]",
                mappings: "[" + Map(1, """[{"gamepad":{"id":1,"buttonId":51}}]""") + "," + Map(2, """[{"gamepad":{"id":1,"buttonId":10}}]""") + "]"));
            // reWASD: 2 right, 3 middle. The mouse state: 1 middle, 2 right.
            var right = Row(p, "LeftTrigger").Sources.First(s => s.DeviceGuid == AllMice);
            Assert.Equal("Button 2", right.Descriptor);
            var middle = Row(p, "RightThumbButton").Sources.First(s => s.DeviceGuid == AllMice);
            Assert.Equal("Button 1", middle.Descriptor);
        }

        [Fact]
        public void MouseMovement_IsAHalfAxisOfAllMice()
        {
            var p = Profile(File(masks: "[" + Mask(1, 23, 64) + "]",
                mappings: "[" + Map(1, """[{"gamepad":{"id":1,"buttonId":47}}]""") + "]"));
            var src = Row(p, "RightThumbAxisY").Sources.First(s => s.DeviceGuid == AllMice);
            Assert.Equal("Axis 1", src.Descriptor);
            Assert.True(src.HalfAxis);
            Assert.True(src.Invert);
            Assert.True(src.InvertOutput);
        }

        [Fact]
        public void NumpadEnter_ReadsApartFromEnter()
        {
            var p = Profile(File(masks: "[" + Mask(1, 11, 156) + "]",
                mappings: "[" + Map(1, """[{"gamepad":{"id":1,"buttonId":1}}]""") + "]"));
            Assert.Contains(Row(p, "ButtonA").Sources, s => s.Descriptor == "Button 136");
        }

        [Theory]
        [InlineData(5, "Gamepad LeftShoulder")]
        [InlineData(7, "Gamepad ButtonBack")]
        [InlineData(9, "Gamepad LeftStick")]
        [InlineData(11, "Gamepad ButtonGuide")]
        [InlineData(12, "Button 11")]
        [InlineData(13, "Button 11")]
        [InlineData(16, "Gamepad Paddle4")]
        [InlineData(17, "Gamepad Paddle3")]
        [InlineData(29, "Gamepad Paddle2")]
        [InlineData(30, "Gamepad Paddle1")]
        [InlineData(31, "Gamepad Paddle4")]
        [InlineData(32, "Gamepad Paddle3")]
        [InlineData(33, "Gamepad DPadUp")]
        [InlineData(99, "Touchpad 0 Click")]
        [InlineData(108, "Touchpad 1 Click")]
        [InlineData(100, "Touchpad 1 DPadUp")]
        [InlineData(163, "Touchpad 0 Tap")]
        public void GamepadButtons_ReadTheirDescriptor(int id, string descriptor)
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, id) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.Equal(descriptor, Row(p, "KbmKey20").Sources[0].Descriptor);
        }

        [Theory]
        [InlineData(40, "Gamepad LeftStickY", true)]
        [InlineData(41, "Gamepad LeftStickY", false)]
        [InlineData(42, "Gamepad LeftStickX", true)]
        [InlineData(43, "Gamepad LeftStickX", false)]
        [InlineData(47, "Gamepad RightStickY", true)]
        [InlineData(50, "Gamepad RightStickX", false)]
        [InlineData(68, "Gyro Pitch", false)]
        [InlineData(69, "Gyro Pitch", true)]
        // Gyro mode looks left and right by roll, reWASD's default.
        [InlineData(70, "Gyro Roll", true)]
        [InlineData(71, "Gyro Roll", false)]
        public void Directions_ReadOneHalf(int id, string descriptor, bool lower)
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, id) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var src = Row(p, "KbmKey20").Sources[0];
            Assert.Equal(descriptor, src.Descriptor);
            Assert.True(src.HalfAxis);
            Assert.Equal(lower, src.Invert);
        }

        [Fact]
        public void StickDirectionInput_FiresPressedTheRightWay()
        {
            var state = new CustomInputState();
            for (int i = 0; i < state.Axis.Length; i++) state.Axis[i] = 32768;
            var p = Profile(File(masks: "[" + Mask(1, 1, 40) + "]", mappings: "[" + Map(1, "[" + Key(17) + "]") + "]"));
            var src = Row(p, "KbmKey57").Sources[0];

            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, src, 50, 0, ""));
            state.Axis[1] = 0; // pushed up
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, src, 50, 0, ""));
            state.Axis[1] = 65535; // pushed down
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, src, 50, 0, ""));
        }

        [Fact]
        public void AnInputPadForgeCannotRead_IsNamedAndSkipped()
        {
            var r = Import(File(masks: "[" + Mask(1, 1, 250) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Skipped && e.ReasonKey == ImportReasons.UnknownInput);
            Assert.Null(Row(r.Profile, "KbmKey20"));
        }

        // ── Outputs as rows ─────────────────────────────────────────────

        [Fact]
        public void HeldKey_IsARowOnTheKeyboardMouseSlot()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.Equal((int)VirtualControllerType.Xbox, p.SlotControllerTypes[0]);
            Assert.Equal((int)VirtualControllerType.KeyboardMouse, p.SlotControllerTypes[1]);
            Assert.Contains(p.SlotMappingSets[1].Rows, r => r.Target == "KbmKey20");
            Assert.DoesNotContain(p.SlotMappingSets[0].Rows, r => r.Target == "KbmKey20");
        }

        [Fact]
        public void HeldChord_IsARowPerKey()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(29) + "," + Key(42) + "," + Key(1) + "]") + "]"));
            Assert.NotNull(Row(p, "KbmKeyA2"));
            Assert.NotNull(Row(p, "KbmKeyA0"));
            Assert.NotNull(Row(p, "KbmKey1B"));
            Assert.Empty(p.Macros);
        }

        [Fact]
        public void HeldKeyWithNoRow_IsAHoldPair()
        {
            // The Windows key is outside the row engine's closed list.
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]", mappings: "[" + Map(1, "[" + Key(219) + "]") + "]"));
            Assert.Null(Row(p, "KbmKey5B"));
            Assert.Equal(2, p.Macros.Length);
            var press = p.Macros[0];
            var release = p.Macros[1];
            Assert.Equal(press.PairId, release.PairId);
            Assert.NotEqual(0, press.PairId);
            Assert.Equal(MacroRepeatMode.UntilRelease, press.RepeatMode);
            Assert.Equal(MacroTriggerMode.OnRelease, release.TriggerMode);
            Assert.Equal(MacroActionType.ToggleKey, Only(press).Type);
            Assert.Equal(0x5B, Only(press).KeyCode);
            Assert.Equal(MacroLatchDirection.On, Only(press).LatchDirection);
            Assert.Equal(MacroLatchDirection.Off, Only(release).LatchDirection);
            Assert.False(press.ConsumeTriggerButtons);
        }

        [Theory]
        [InlineData("""{"mouse":{"buttonId":1}}""", "KbmMBtn0")]
        [InlineData("""{"mouse":{"buttonId":2}}""", "KbmMBtn1")]
        [InlineData("""{"mouse":{"buttonId":3}}""", "KbmMBtn2")]
        [InlineData("""{"gamepad":{"id":1,"buttonId":1}}""", "ButtonA")]
        [InlineData("""{"gamepad":{"id":1,"buttonId":33}}""", "DPadUp")]
        [InlineData("""{"gamepad":{"id":1,"buttonId":51}}""", "LeftTrigger")]
        public void OutputsLandOnTheirTarget(string step, string target)
        {
            var p = Profile(File(masks: "[" + Mask(1, 11, 30) + "]", mappings: "[" + Map(1, "[" + step + "]") + "]"));
            Assert.Contains(Row(p, target).Sources, s => s.Descriptor == "Button 65" && s.DeviceGuid == AllKeyboards);
        }

        [Theory]
        [InlineData(40, "LeftThumbAxisY", -1)]
        [InlineData(41, "LeftThumbAxisY", 1)]
        [InlineData(42, "LeftThumbAxisX", -1)]
        [InlineData(43, "LeftThumbAxisX", 1)]
        [InlineData(49, "RightThumbAxisX", -1)]
        public void ButtonToStickDirection_ReadsTheRightSign(int output, string target, int sign)
        {
            var state = new CustomInputState();
            state.Buttons[65] = true;
            var p = Profile(File(masks: "[" + Mask(1, 11, 30) + "]",
                mappings: "[" + Map(1, "[{\"gamepad\":{\"id\":1,\"buttonId\":" + output + "}}]") + "]"));
            var src = Row(p, target).Sources.First(s => s.Descriptor == "Button 65");
            float v = SourceCoercion.EvaluateForBipolarAxisTarget(state, src, 0, false, "");
            Assert.Equal(sign, Math.Sign(v));
        }

        [Fact]
        public void PartialStickValue_RidesAHeldAxis_BecauseAPressReadsFullOnARow()
        {
            var p = Profile(File(masks: "[" + Mask(1, 11, 30) + "]",
                mappings: "[" + Map(1, """[{"gamepad":{"id":1,"buttonId":43,"absolute":true,"value":16384}}]""") + "]"));
            Assert.DoesNotContain(Row(p, "LeftThumbAxisX").Sources, s => s.Descriptor == "Button 65");
            var press = p.Macros[0];
            var a = Only(press);
            Assert.Equal(MacroActionType.AxisSetLatched, a.Type);
            Assert.Equal(MacroAxisTarget.LeftStickX, a.AxisTarget);
            Assert.InRange(a.AxisValue, 16000, 16800);
            Assert.Equal(MacroActionType.AxisLatchRelease, Only(p.Macros[1]).Type);
            Assert.Equal(MacroAxisTarget.LeftStickX, Only(p.Macros[1]).AxisTarget);
        }

        [Fact]
        public void FullStickValue_StaysARow()
        {
            var p = Profile(File(masks: "[" + Mask(1, 11, 30) + "]",
                mappings: "[" + Map(1, """[{"gamepad":{"id":1,"buttonId":43,"absolute":true,"value":32767}}]""") + "]"));
            Assert.Contains(Row(p, "LeftThumbAxisX").Sources, s => s.Descriptor == "Button 65");
            Assert.Empty(p.Macros);
        }

        [Fact]
        public void RelativeStickValue_AddsToTheStickWhileHeld()
        {
            var r = Import(File(masks: "[" + Mask(1, 11, 30) + "]",
                mappings: "[" + Map(1, """[{"gamepad":{"id":1,"buttonId":43,"absolute":false,"compensation":true,"value":1000}}]""") + "]"));
            var m = Assert.Single(r.Profile.Macros);
            Assert.Equal(MacroTriggerMode.OnPress, m.TriggerMode);
            Assert.Equal(MacroRepeatMode.UntilRelease, m.RepeatMode);
            Assert.Equal(0, m.RepeatDelayMs);
            var a = Only(m);
            Assert.Equal(MacroActionType.AxisAdd, a.Type);
            Assert.Equal(MacroAxisTarget.LeftStickX, a.AxisTarget);
            Assert.True(a.AxisValue > 0);
            // Compensation has no published meaning, so its line is the one
            // the report holds.
            var line = Assert.Single(r.Report.Entries, e => e.Status != TranslationStatus.Clean);
            Assert.Equal(ImportReasons.ControllerSetting, line.ReasonKey);
            Assert.Contains("compensation", line.ReasonArgs);
        }

        [Theory]
        [InlineData("up", "KbmMouseY", true)]
        [InlineData("down", "KbmMouseY", false)]
        [InlineData("left", "KbmMouseX", true)]
        [InlineData("right", "KbmMouseX", false)]
        public void MouseDirections(string dir, string target, bool invert)
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[{\"mouse\":{\"direction\":\"" + dir + "\"}}]") + "]"));
            Assert.Equal(invert, Row(p, target).Sources[0].Invert);
        }

        [Theory]
        [InlineData("up", "KbmScroll", true)]
        [InlineData("down", "KbmScroll", false)]
        [InlineData("left", "KbmScrollH", true)]
        [InlineData("right", "KbmScrollH", false)]
        public void WheelDirections(string dir, string target, bool invert)
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[{\"mouse\":{\"wheel\":\"" + dir + "\"}}]") + "]"));
            Assert.Equal(invert, Row(p, target).Sources[0].Invert);
        }

        [Fact]
        public void APadButtonAVirtualXboxLacks_IsNamedAndSkipped()
        {
            var r = Import(File(masks: "[" + Mask(1, 11, 30) + "]",
                mappings: "[" + Map(1, """[{"gamepad":{"id":1,"buttonId":29,"description":"LeftUpperPaddle"}}]""") + "]"));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.NoSuchPadButton && e.ReasonArgs.Contains("LeftUpperPaddle"));
        }

        // ── Masks with more than one input ──────────────────────────────

        private const string TwoButtons = """{"id":1,"set":[{"deviceId":1,"buttonId":5},{"deviceId":1,"buttonId":1}]}""";

        [Fact]
        public void TwoButtonMask_IsARowWithAGate()
        {
            var p = Profile(File(masks: "[" + TwoButtons + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var src = Row(p, "KbmKey20").Sources[0];
            Assert.Equal("Gamepad LeftShoulder", src.Descriptor);
            Assert.Equal("Gamepad ButtonA", src.GateDescriptor);
            Assert.Empty(p.Macros);
        }

        [Fact]
        public void ThreeButtonMask_UsesBothGates()
        {
            var p = Profile(File(masks: """[{"id":1,"set":[{"deviceId":1,"buttonId":5},{"deviceId":1,"buttonId":1},{"deviceId":1,"buttonId":2}]}]""",
                mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var src = Row(p, "KbmKey20").Sources[0];
            Assert.Equal("Gamepad ButtonA", src.GateDescriptor);
            Assert.Equal("Gamepad ButtonB", src.Gate2Descriptor);
        }

        [Fact]
        public void FourInputMask_IsAMacroWithFourEntries()
        {
            var p = Profile(File(masks: """[{"id":1,"set":[{"deviceId":1,"buttonId":5},{"deviceId":1,"buttonId":1},{"deviceId":1,"buttonId":2},{"deviceId":1,"buttonId":3}]}]""",
                mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.Null(Row(p, "KbmKey20"));
            Assert.Equal(4, p.Macros[0].TriggerInputs.Split('|').Length);
        }

        [Fact]
        public void MaskAcrossDevices_IsAMacro_BecauseAGateReadsTheSourcesDevice()
        {
            var p = Profile(File(masks: """[{"id":1,"set":[{"deviceId":1,"buttonId":5},{"deviceId":11,"buttonId":30}]}]""",
                mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.Null(Row(p, "KbmKey20"));
            var specs = p.Macros[0].TriggerInputs.Split('|');
            Assert.Equal(2, specs.Length);
            Assert.Contains(specs, s => s.Contains(AllKeyboards) && s.EndsWith(":btn:65"));
        }

        // ── Activators ──────────────────────────────────────────────────

        [Theory]
        [InlineData("start", MacroTriggerMode.OnPress)]
        [InlineData("release", MacroTriggerMode.OnRelease)]
        [InlineData("long", MacroTriggerMode.HoldForMs)]
        [InlineData("double", MacroTriggerMode.DoublePress)]
        [InlineData("triple", MacroTriggerMode.TriplePress)]
        [InlineData("single", MacroTriggerMode.OnPress)]
        public void OneTimeActivators_MapToTheirTrigger(string type, MacroTriggerMode mode)
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]", type: type, mode: "onetime") + "]"));
            var m = Assert.Single(p.Macros);
            Assert.Equal(mode, m.TriggerMode);
            Assert.Equal(MacroRepeatMode.Once, m.RepeatMode);
            Assert.Equal(MacroActionType.KeyPress, Only(m).Type);
            Assert.Equal(0x20, Only(m).KeyCode);
        }

        [Fact]
        public void LongPress_DefaultsToReWasds300Ms_AndCarriesItsParameter()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 1, 2) + "]", mappings: "["
                + Map(1, "[" + Key(57) + "]", type: "long", mode: "onetime") + ","
                + Map(2, "[" + Key(28) + "]", type: "long", mode: "onetime", activatorParams: """{"longwaittime":2500}""") + "]"));
            Assert.Equal(300, p.Macros[0].TriggerHoldMs);
            Assert.Equal(2500, p.Macros[1].TriggerHoldMs);
        }

        [Fact]
        public void SingleBesideDouble_WaitsOutTheWindow()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]", mappings: "["
                + Map(1, "[" + Key(57) + "]") + "," + Map(1, "[" + Key(28) + "]", type: "double") + "]"));
            Assert.Null(Row(p, "KbmKey20"));
            var single = p.Macros.First(m => m.TriggerMode == MacroTriggerMode.SinglePress);
            Assert.Equal(MacroActionType.ToggleKey, single.Actions[0].Type);
            Assert.Equal(0x20, single.Actions[0].KeyCode);
            var dbl = p.Macros.First(m => m.TriggerMode == MacroTriggerMode.DoublePress);
            Assert.Equal(0x0D, dbl.Actions[0].KeyCode);
        }

        [Fact]
        public void SingleBesideLong_IsAShortPressSharingTheThreshold()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]", mappings: "["
                + Map(1, "[" + Key(57) + "]") + ","
                + Map(1, "[" + Key(28) + "]", type: "long", mode: "onetime", activatorParams: """{"longwaittime":731}""") + "]"));
            var tap = p.Macros.First(m => m.TriggerMode == MacroTriggerMode.ShortPress);
            Assert.Equal(731, tap.TriggerHoldMs);
            var hold = p.Macros.First(m => m.TriggerMode == MacroTriggerMode.HoldForMs);
            Assert.Equal(731, hold.TriggerHoldMs);
        }

        [Fact]
        public void HeldSingleBesideAGesture_HoldsUntilTheRelease()
        {
            var r = Import(File(masks: "[" + Mask(1, 1, 1) + "]", mappings: "["
                + Map(1, "[" + Key(57) + "]") + "," + Map(1, "[" + Key(28) + "]", type: "double") + "]"));
            var press = r.Profile.Macros.First(m => m.TriggerMode == MacroTriggerMode.SinglePress);
            Assert.Equal(MacroRepeatMode.UntilRelease, press.RepeatMode);
            Assert.Equal(MacroLatchDirection.On, press.Actions[0].LatchDirection);
            Assert.Equal(0x20, press.Actions[0].KeyCode);
            // Fired with the button already up, the pass is one tap this long.
            Assert.Equal(MacroActionType.Delay, press.Actions[^1].Type);
            Assert.Equal(50, press.Actions[^1].DurationMs);
            var twin = r.Profile.Macros.Single(m => m.PairId == press.PairId && !ReferenceEquals(m, press));
            Assert.Equal(MacroTriggerMode.OnRelease, twin.TriggerMode);
            Assert.Equal(MacroLatchDirection.Off, Only(twin).LatchDirection);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void HeldLongPress_IsAHoldPairOnThatTrigger()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]", type: "long") + "]"));
            Assert.Equal(2, p.Macros.Length);
            Assert.Equal(MacroTriggerMode.HoldForMs, p.Macros[0].TriggerMode);
            Assert.Equal(MacroLatchDirection.On, Only(p.Macros[0]).LatchDirection);
            Assert.Equal(MacroTriggerMode.OnRelease, p.Macros[1].TriggerMode);
        }

        [Fact]
        public void RepeatMode_RunsWhileHeld()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]", mode: "repeat") + "]"));
            var m = Assert.Single(p.Macros);
            Assert.Equal(MacroTriggerMode.WhileHeld, m.TriggerMode);
            Assert.Equal(MacroRepeatMode.UntilRelease, m.RepeatMode);
        }

        [Fact]
        public void TurboMode_IsTurbo()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]", mode: "turbo") + "]"));
            Assert.Equal(MacroTriggerMode.Turbo, Assert.Single(p.Macros).TriggerMode);
        }

        [Fact]
        public void ToggleMode_FlipsTheOutputOnEachPress()
        {
            var p = Profile(File(masks: "[" + Mask(1, 11, 42) + "]",
                mappings: "[" + Map(1, "[" + Key(42) + "]", mode: "toggle") + "]"));
            var m = Assert.Single(p.Macros);
            Assert.Equal(MacroTriggerMode.OnPress, m.TriggerMode);
            Assert.Equal(MacroActionType.ToggleKey, Only(m).Type);
            Assert.Equal(MacroLatchDirection.Toggle, Only(m).LatchDirection);
        }

        [Fact]
        public void ToggleOnALongPress_TheNextPressTurnsItOff()
        {
            var r = Import(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]", type: "long", mode: "toggle") + "]"));
            Assert.Equal(2, r.Profile.Macros.Length);
            var off = r.Profile.Macros[0];
            Assert.Equal(MacroTriggerMode.OnPress, off.TriggerMode);
            Assert.Equal(MacroLatchDirection.Off, Only(off).LatchDirection);
            var on = r.Profile.Macros[1];
            Assert.Equal(MacroTriggerMode.HoldForMs, on.TriggerMode);
            Assert.Equal(MacroLatchDirection.On, Only(on).LatchDirection);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void ToggledSequenceOnALongPress_RunsUntilTheNextPress()
        {
            var r = Import(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + """,{"pause":{"value":50}},""" + Key(28) + "]", type: "long", mode: "toggle") + "]"));
            var m = r.Profile.Macros.Single(x => x.TriggerMode == MacroTriggerMode.HoldForMs);
            Assert.Equal(MacroRepeatMode.UntilNextPress, m.RepeatMode);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void ToggleOverASequence_UsesTheToggleTrigger()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + """,{"pause":{"value":50}},""" + Key(28) + "]", mode: "toggle") + "]"));
            var m = Assert.Single(p.Macros);
            Assert.Equal(MacroTriggerMode.Toggle, m.TriggerMode);
            Assert.Equal(MacroRepeatMode.UntilRelease, m.RepeatMode);
        }

        [Fact]
        public void ExpertMacroFlag_MakesAPairNotARow()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(29) + "," + Key(46) + "]", activatorParams: """{"macro":true}""") + "]"));
            Assert.Null(Row(p, "KbmKeyA2"));
            Assert.Equal(2, p.Macros.Length);
            Assert.Equal(2, p.Macros[0].Actions.Length);
        }

        // ── Macro steps ─────────────────────────────────────────────────

        [Fact]
        public void ExplicitDownAndUp_AreLatches_WithTheirPause()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]", mappings: "["
                + Map(1, "[" + Key(30, "down") + """,{"pause":{"value":40}},""" + Key(30, "up") + "]", mode: "onetime") + "]"));
            var a = Assert.Single(p.Macros).Actions;
            Assert.Equal(MacroActionType.ToggleKey, a[0].Type);
            Assert.Equal(MacroLatchDirection.On, a[0].LatchDirection);
            Assert.Equal(MacroActionType.Delay, a[1].Type);
            Assert.Equal(40, a[1].DurationMs);
            Assert.Equal(MacroLatchDirection.Off, a[2].LatchDirection);
        }

        [Fact]
        public void PadStepsInAMacro_LatchTheVirtualButton()
        {
            var p = Profile(File(masks: "[" + Mask(1, 11, 30) + "]", mappings: "["
                + Map(1, """[{"gamepad":{"id":1,"buttonId":1,"action":"down"}},{"gamepad":{"id":1,"buttonId":1,"action":"up"}}]""", mode: "onetime") + "]"));
            var a = Assert.Single(p.Macros).Actions;
            Assert.Equal(MacroActionType.ToggleVcButton, a[0].Type);
            Assert.Equal(Gamepad.A, a[0].ButtonFlags);
            Assert.Equal(MacroLatchDirection.On, a[0].LatchDirection);
            Assert.Equal(MacroLatchDirection.Off, a[1].LatchDirection);
        }

        [Fact]
        public void StickStepInAMacro_IsAnAxisInTheXInputFrame()
        {
            var p = Profile(File(masks: "[" + Mask(1, 11, 30) + "]", mappings: "["
                + Map(1, """[{"gamepad":{"id":1,"buttonId":40,"absolute":true,"value":32767}}]""", mode: "onetime") + "]"));
            var a = Only(Assert.Single(p.Macros));
            Assert.Equal(MacroActionType.AxisHold, a.Type);
            Assert.Equal(MacroAxisTarget.LeftStickY, a.AxisTarget);
            // Up is positive on an XInput thumb.
            Assert.True(a.AxisValue > 0);
        }

        [Fact]
        public void WheelStep_IsADetentUpPositive()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, """[{"mouse":{"wheel":"up"}}]""", mode: "onetime") + "]"));
            var a = Only(Assert.Single(p.Macros));
            Assert.Equal(MacroActionType.MouseWheelTap, a.Type);
            Assert.Equal(1, a.AxisValue);
        }

        [Fact]
        public void RumbleStep_CarriesMotorsAndDuration()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]", mappings: "["
                + Map(1, """[{"rumble":{"duration":250,"amplitude":{"motor":{"left":80,"right":20},"trigger":{"left":10,"right":0}}}}]""", mode: "onetime") + "]"));
            var a = Assert.Single(p.Macros).Actions;
            Assert.Equal(MacroActionType.Rumble, a[0].Type);
            Assert.Equal(80, a[0].RumbleStrengthLeft);
            Assert.Equal(20, a[0].RumbleStrengthRight);
            Assert.Equal(250, a[0].RumbleHoldMs);
            Assert.Equal(MacroActionType.RumbleTrigger, a[1].Type);
        }

        [Fact]
        public void BreakStep_IsAComboBreak()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]", mappings: "["
                + Map(1, "[" + Key(57) + """,{"break":{}},""" + Key(28) + "]", mode: "onetime") + "]"));
            Assert.Contains(Assert.Single(p.Macros).Actions, a => a.Type == MacroActionType.ComboBreak);
        }

        // ── Commands ────────────────────────────────────────────────────

        private static ForeignImportResult Command(int type, string name, string more = "")
            => Import(File(commands: "[{\"id\":1,\"type\":" + type + ",\"name\":\"" + name + "\"" + more + "}]",
                masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, """[{"command":{"id":1}}]""", mode: "onetime") + "]"));

        [Theory]
        [InlineData(6, MacroSetGyroEngagedMode.Toggle)]
        [InlineData(15, MacroSetGyroEngagedMode.On)]
        [InlineData(16, MacroSetGyroEngagedMode.Off)]
        public void GyroCommands_SetTheEngage(int type, MacroSetGyroEngagedMode mode)
        {
            var a = Only(Assert.Single(Command(type, "x").Profile.Macros));
            Assert.Equal(MacroActionType.SetGyroEngaged, a.Type);
            Assert.Equal(mode, a.SetGyroEngagedMode);
        }

        [Theory]
        [InlineData(5)]
        [InlineData(18)]
        public void ResetCommands_RecenterGyro(int type)
            => Assert.Equal(MacroActionType.GyroRecenter, Only(Assert.Single(Command(type, "x").Profile.Macros)).Type);

        [Theory]
        [InlineData(7, "shutdown.exe", "/l")]
        [InlineData(8, "shutdown.exe", "/s /t 0")]
        [InlineData(13, "shutdown.exe", "/h")]
        [InlineData(17, "shutdown.exe", "/r /t 0")]
        public void PowerCommands_RunWindowsOwnCommand_AndThePreviewNamesThem(int type, string program, string args)
        {
            var r = Command(type, "COMMAND_POWER");
            var m = Assert.Single(r.Profile.Macros);
            var a = Only(m);
            Assert.Equal(MacroActionType.RunProgram, a.Type);
            Assert.Equal(program, a.ProgramPath);
            Assert.Equal(args, a.ProgramArgs);
            Assert.Contains("COMMAND_POWER", r.Runs);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void SleepCommand_Suspends()
        {
            var a = Only(Assert.Single(Command(9, "COMMAND_SLEEP").Profile.Macros));
            Assert.Equal("powershell.exe", a.ProgramPath);
            Assert.Contains("SetSuspendState('Suspend'", a.ProgramArgs);
        }

        [Fact]
        public void AppLaunchCommand_RunsItsProgram_OnlyWhenPressed_AndThePreviewNamesIt()
        {
            var r = Command(1000, "x", ""","params":{"path":"C:\\Games\\tool.exe"}""");
            var m = Assert.Single(r.Profile.Macros);
            var a = Only(m);
            Assert.Equal(MacroActionType.RunProgram, a.Type);
            Assert.Equal("C:\\Games\\tool.exe", a.ProgramPath);
            Assert.Contains("C:\\Games\\tool.exe", r.Runs);
            Assert.NotEqual(MacroTriggerMode.Always, m.TriggerMode);
            Assert.False(string.IsNullOrEmpty(m.TriggerInputs));
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void CloseTaskCommand_SendsAltF4()
        {
            var r = Command(12, "COMMAND_CLOSE_ACTIVE_TASK");
            var a = Only(Assert.Single(r.Profile.Macros));
            Assert.Equal(MacroActionType.KeyPress, a.Type);
            Assert.Equal(new[] { 0xA4, 0x73 }, MacroAction.ParseKeyString(a.KeyString));
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Theory]
        [InlineData(101, 1)]
        [InlineData(103, 3)]
        [InlineData(120, 0)]
        public void CrosshairCommand_TogglesAScreenOverlay_AndSaysThePictureIsYours(int type, int overlay)
        {
            var r = Command(type, "x");
            var a = Only(Assert.Single(r.Profile.Macros));
            Assert.Equal(MacroActionType.ToggleScreenOverlay, a.Type);
            Assert.Equal(overlay, a.ScreenOverlayNumber);
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Partial && e.ReasonKey == ImportReasons.Crosshair);
        }

        [Theory]
        [InlineData(1, SwitchProfileMode.ToggleVCsDisabled)]
        [InlineData(3, SwitchProfileMode.ToggleWindow)]
        public void RemapAndWindowCommands_AreProfileShortcuts(int type, SwitchProfileMode mode)
        {
            var a = Only(Assert.Single(Command(type, "x").Profile.Macros));
            Assert.Equal(MacroActionType.ProfileShortcut, a.Type);
            Assert.Equal(mode, a.ProfileShortcutMode);
        }

        [Fact]
        public void ControllerOffCommand_DisconnectsTheSlotsDevices()
        {
            var a = Only(Assert.Single(Command(2, "x").Profile.Macros));
            Assert.Equal(MacroActionType.DisconnectController, a.Type);
            Assert.Equal(MacroDisconnectTarget.SlotDevices, a.DisconnectTarget);
        }

        [Fact]
        public void ACommandStep_NamesTheCommandInThePreview()
        {
            var r = Command(8, "Shut down");
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Clean && e.Binding.Contains("Shut down"));
        }

        [Fact]
        public void UnknownCommand_IsNamed()
        {
            var r = Command(10, "COMMAND_ANTIBOSS");
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.UnknownCommand && e.ReasonArgs.Contains("COMMAND_ANTIBOSS"));
        }

        // ── Layers ──────────────────────────────────────────────────────

        private const string OneLayer = """[{"id":1,"type":"default","description":"Combat"}]""";

        internal static string Jump(int mask, int layer, string type = "single", string mode = "hold_until_release", int shift = 0, string jumpMore = "")
            => Map(mask, "", type, mode, shift, ",\"jumpToLayer\":{\"layer\":" + layer + jumpMore + "}");

        [Fact]
        public void HoldJump_AndItsReleaseJumpBack_AreOneHoldActivator()
        {
            var r = Import(File(shifts: OneLayer, masks: "[" + Mask(1, 1, 5) + "]", mappings: "["
                + Jump(1, 1) + "," + Jump(1, 0, "release", "onetime", 1) + "]"));
            var act = Assert.Single(r.Profile.SlotMappingSets[0].ShiftActivators);
            Assert.Equal("Hold", act.Mode);
            Assert.Equal("Shift1", act.LayerMask);
            Assert.Equal("Combat", act.LayerName);
            Assert.Equal("Gamepad LeftShoulder", act.Descriptor);
            Assert.True(act.InheritUnmapped);
            Assert.Empty(r.Profile.Macros);
        }

        [Fact]
        public void ToggleJump_IsAToggleActivator()
        {
            var p = Profile(File(shifts: OneLayer, masks: "[" + Mask(1, 1, 5) + "]", mappings: "["
                + Jump(1, 1, mode: "toggle") + "," + Jump(1, 0, mode: "toggle", shift: 1) + "]"));
            Assert.Equal("Toggle", Assert.Single(p.SlotMappingSets[0].ShiftActivators).Mode);
            Assert.Empty(p.Macros);
        }

        [Fact]
        public void JumpDelayAndPostpone_RideTheActivator()
        {
            var p = Profile(File(shifts: OneLayer, masks: "[" + Mask(1, 1, 5) + "]", mappings: "["
                + Jump(1, 1, jumpMore: ",\"delay\":250,\"postpone\":true") + "]"));
            var act = Assert.Single(p.SlotMappingSets[0].ShiftActivators);
            Assert.Equal(250, act.DelayMs);
            Assert.True(act.PostponeMapping);
        }

        [Fact]
        public void JumpInsideALayer_NamesItsHost()
        {
            var p = Profile(File(shifts: """[{"id":1,"type":"default"},{"id":2,"type":"default"}]""",
                masks: "[" + Mask(1, 1, 5) + "," + Mask(2, 1, 6) + "]", mappings: "["
                + Jump(1, 1) + "," + Jump(2, 2, shift: 1) + "]"));
            var second = p.SlotMappingSets[0].ShiftActivators.First(a => a.LayerMask == "Shift2");
            Assert.Equal("Shift1", second.HostLayerMask);
        }

        [Fact]
        public void OneShotJump_IsASwitchLayerMacro_ToALayerThatExists()
        {
            var p = Profile(File(shifts: OneLayer, masks: "[" + Mask(1, 1, 5) + "]", mappings: "["
                + Jump(1, 1, "double", "onetime") + "]"));
            var m = Assert.Single(p.Macros);
            Assert.Equal(MacroTriggerMode.DoublePress, m.TriggerMode);
            Assert.Equal(MacroActionType.SwitchLayer, Only(m).Type);
            Assert.Equal("Shift1", Only(m).SwitchLayerMask);
            // ApplyMacroLayerSwitch ignores a mask the slot does not declare.
            Assert.Contains(p.SlotMappingSets[0].ShiftActivators, a => a.LayerMask == "Shift1" && a.Mode == "Passive");
        }

        [Fact]
        public void LayerNothingOpens_StaysAsANoButtonLayer_AndSaysSo()
        {
            // Base has mappings of its own, so this is a config with a layer
            // it cannot reach, not a preset.
            var r = Import(File(shifts: OneLayer, masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 1, 2) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]", shift: 1) + "," + Map(2, "[" + Key(28) + "]") + "]"));
            var act = Assert.Single(r.Profile.SlotMappingSets[0].ShiftActivators);
            Assert.Equal("Passive", act.Mode);
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.LayerNoInput);
        }

        [Fact]
        public void LayerSet_IsItsOwnHoldOrToggle()
        {
            var p = Profile(File(shifts: """[{"id":1,"type":"default","set":[{"deviceId":11,"buttonId":29}]},{"id":2,"type":"toggle","set":[{"deviceId":11,"buttonId":219}]}]"""));
            var sets = p.SlotMappingSets[0].ShiftActivators;
            var hold = sets.First(a => a.LayerMask == "Shift1");
            Assert.Equal("Hold", hold.Mode);
            Assert.Equal("Button 162", hold.Descriptor);
            Assert.Equal(AllKeyboards, hold.DeviceGuid);
            Assert.Equal("Toggle", sets.First(a => a.LayerMask == "Shift2").Mode);
        }

        [Fact]
        public void TriggerAsJump_IsAnAxisActivator()
        {
            var p = Profile(File(shifts: OneLayer, masks: "[" + Mask(1, 1, 51) + "]", mappings: "[" + Jump(1, 1) + "]"));
            var act = Assert.Single(p.SlotMappingSets[0].ShiftActivators);
            Assert.Equal("Axis", act.Kind);
            Assert.Equal("Gamepad LeftTrigger", act.Descriptor);
        }

        [Fact]
        public void ChordJump_IsAChordActivator()
        {
            var p = Profile(File(shifts: OneLayer, masks: "[" + TwoButtons + "]", mappings: "[" + Jump(1, 1) + "]"));
            var act = Assert.Single(p.SlotMappingSets[0].ShiftActivators);
            Assert.Equal("Chord", act.Kind);
            Assert.Equal("Gamepad ButtonA", act.ChordSecondDescriptor);
        }

        [Fact]
        public void ALayerOverridesByInput_NotByOutput()
        {
            // Base: A sends Space, and X passes through as X. The layer sends
            // X from A. Holding the layer, A must stop sending Space, and the
            // physical X must keep sending X. A plain layer row would do
            // neither: rows override by output.
            var p = Profile(File(shifts: OneLayer, masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 1, 5) + "]", mappings: "["
                + Jump(2, 1) + ","
                + Map(1, "[" + Key(57) + "]") + ","
                + Map(1, """[{"gamepad":{"id":1,"buttonId":3}}]""", shift: 1) + "]"));

            var layerX = Row(p, "ButtonX", "Shift1");
            Assert.Contains(layerX.Sources, s => s.Descriptor == "Gamepad ButtonA");
            Assert.Contains(layerX.Sources, s => s.Descriptor == "Gamepad ButtonX");

            var layerSpace = Row(p, "KbmKey20", "Shift1");
            Assert.True(layerSpace.NoInherit);
            Assert.Empty(layerSpace.Sources);

            // An output the layer leaves alone has no layer row, so it inherits.
            Assert.Null(Row(p, "ButtonY", "Shift1"));
        }

        [Fact]
        public void ALayerReplacingABaseMacro_DoesNotInherit_AndKeepsTheOtherMacros()
        {
            // "Base" stays open on an inheriting layer, so the only exact form
            // for two macros on one button is a layer that does not inherit,
            // with its own copy of everything it keeps.
            var p = Profile(File(shifts: OneLayer, masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 1, 2) + "," + Mask(3, 1, 5) + "]", mappings: "["
                + Jump(3, 1) + ","
                + Map(1, "[" + Key(57) + "]", mode: "onetime") + ","
                + Map(2, "[" + Key(28) + "]", mode: "onetime") + ","
                + Map(1, "[" + Key(15) + "]", mode: "onetime", shift: 1) + "]"));

            Assert.False(p.SlotMappingSets[0].ShiftActivators.Single().InheritUnmapped);

            var space = p.Macros.Where(m => m.Actions.Any(a => a.KeyCode == 0x20)).ToList();
            Assert.Equal("Base", Assert.Single(space).LayerMask);

            var enter = p.Macros.Where(m => m.Actions.Any(a => a.KeyCode == 0x0D)).Select(m => m.LayerMask).ToList();
            Assert.Contains("Base", enter);
            Assert.Contains("Shift1", enter);

            var tab = p.Macros.Where(m => m.Actions.Any(a => a.KeyCode == 0x09)).ToList();
            Assert.Equal("Shift1", Assert.Single(tab).LayerMask);

            // Rows the layer keeps are written on it, since Base closes there.
            Assert.Contains(Row(p, "ButtonB", "Shift1").Sources, s => s.Descriptor == "Gamepad ButtonB");
        }

        [Fact]
        public void HoldPairsCopiedToALayer_KeepTheirOwnPairId()
        {
            var p = Profile(File(shifts: OneLayer, masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 1, 2) + "," + Mask(3, 1, 5) + "]", mappings: "["
                + Jump(3, 1) + ","
                + Map(1, "[" + Key(57) + "]", mode: "onetime") + ","
                + Map(2, "[" + Key(219) + "]") + ","
                + Map(1, "[" + Key(15) + "]", mode: "onetime", shift: 1) + "]"));
            var pairs = p.Macros.Where(m => m.PairId != 0).GroupBy(m => m.PairId).ToList();
            Assert.Equal(2, pairs.Count);
            Assert.All(pairs, g => Assert.Equal(2, g.Count()));
            Assert.Single(pairs, g => g.All(m => m.LayerMask == "Base"));
            Assert.Single(pairs, g => g.All(m => m.LayerMask == "Shift1"));
        }

        [Fact]
        public void UnheritableMask_BlocksItsBaseOutputOnTheLayer()
        {
            var p = Profile(File(shifts: """[{"id":1,"type":"default","unheritableMasks":[1]}]""",
                masks: "[" + Mask(1, 1, 1) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.True(Row(p, "KbmKey20", "Shift1").NoInherit);
            Assert.True(Row(p, "ButtonA", "Shift1").NoInherit);
        }

        [Fact]
        public void UnmapOnALayer_BlocksOnThatLayerOnly()
        {
            var p = Profile(File(shifts: OneLayer, masks: "[" + Mask(3, 1, 5) + "]",
                mappings: "[" + Jump(3, 1) + "," + """{"condition":{"shiftId":1,"hardware":{"deviceId":1,"buttonId":1}},"hardware":{"unmap":true}}]"""));
            Assert.NotNull(Row(p, "ButtonA"));
            Assert.True(Row(p, "ButtonA", "Shift1").NoInherit);
        }

        [Fact]
        public void KeyboardUnlockLayer_StopsBasesKeyboardMappings_AndKeepsTheController()
        {
            // An unlock layer hands the keyboard back to Windows: Base's
            // keyboard mappings stop on it, and a controller's carry on.
            var r = Import(File(shifts: """[{"id":1,"type":"unlockKeyboardOnly"}]""",
                masks: "[" + Mask(1, 11, 30) + "," + Mask(2, 1, 2) + "," + Mask(3, 1, 5) + "]",
                mappings: "[" + Jump(3, 1) + ","
                    + Map(1, "[{\"gamepad\":{\"id\":1,\"buttonId\":1}}]") + ","
                    + Map(2, "[{\"gamepad\":{\"id\":1,\"buttonId\":3}}]") + "]"));
            var p = r.Profile;
            bool Keyboard(MappingSource s) => string.Equals(s.DeviceGuid, ImportBuilder.AllKeyboardsGuid, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Row(p, "ButtonA").Sources, Keyboard);
            // The layer's A row keeps the pad's own A and drops the key.
            Assert.NotNull(Row(p, "ButtonA", "Shift1"));
            Assert.DoesNotContain(Row(p, "ButtonA", "Shift1").Sources, Keyboard);
            Assert.Null(Row(p, "ButtonX", "Shift1"));
            // The layer does what reWASD's does, so it reports Clean with no reason.
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == "Import_Tr_UnlockLayer");
            var line = Assert.Single(r.Report.Entries, e => e.Binding == "unlockKeyboardOnly");
            Assert.Equal(TranslationStatus.Clean, line.Status);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Devices_AllKeyboardsMerged, line.Emitted);
        }

        [Fact]
        public void UnlockLayer_ForBoth_IsCleanForEach()
        {
            var r = Import(File(shifts: """[{"id":1,"type":"unlock"}]"""));
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == "Import_Tr_UnlockLayer");
            Assert.Equal(2, r.Report.Entries.Count(e => e.Binding == "unlock" && e.Status == TranslationStatus.Clean));
        }

        // ── Tuning ──────────────────────────────────────────────────────

        private static string Tuned(string scope)
            => "[{\"id\":1,\"name\":\"gamepad\",\"convertToVirtual\":true,\"default\":" + scope + "}]";

        [Fact]
        public void AxisRange_LeavesTheGamesStickAlone_AndSaysItDidNotComeAcross()
        {
            // A stick's "sensitivity" is reWASD's Horizontal and Vertical axis
            // range: the width of the direction zones that keys mapped to the
            // stick read. reWASDUI's StickCrossSectors draws it as those zones,
            // and reWASD's administrator on its forum (thread 222207): "it
            // doesn't influence a virtual stick".
            var r = Import(File(hardware: Tuned("""{"sticks":[{"name":"left","sensitivity":{"x":16384,"y":32767}}]}""")));
            Assert.Equal(1.0, Row(r.Profile, "LeftThumbAxisX").Sources[0].Sensitivity);
            Assert.Equal(1.0, Row(r.Profile, "LeftThumbAxisY").Sources[0].Sensitivity);
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Partial
                && e.ReasonKey == ImportReasons.ControllerSetting && e.ReasonArgs.Contains("sensitivity"));
        }

        [Fact]
        public void LowZone_IsTheDirectionThreshold_AndLeavesTheGamesStickAlone()
        {
            // reWASD's help: zones change nothing the game reads, and only
            // "Affect gamepad behavior for games" puts the dead zone there.
            var p = Profile(File(hardware: """[{"id":1,"name":"gamepad","convertToVirtual":true,"default":{"sticks":[{"name":"right","zones":[{"name":"low","x":16384}]}]}}]""",
                masks: "[" + Mask(1, 1, 47) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var axis = Row(p, "RightThumbAxisX").Sources[0];
            Assert.Equal(0, axis.ParamStickDeadZoneShape);
            Assert.Equal(0.0, axis.ParamStickDeadZoneInner);
            Assert.Equal(50, Row(p, "KbmKey20").Sources[0].DeadZone);
        }

        [Fact]
        public void Rotation_TurnsTheStickCounterclockwise_AsReWasdDoes()
        {
            var p = Profile(File(hardware: Tuned("""{"sticks":[{"name":"left","rotation":30}]}""")));
            var x = Row(p, "LeftThumbAxisX");
            var y = Row(p, "LeftThumbAxisY");
            Assert.Equal("Sum", x.CombineMode);
            Assert.Equal("Sum", y.CombineMode);
            Assert.Equal(2, x.Sources.Count);
            Assert.Equal(2, y.Sources.Count);

            // reWASD turns a stick counterclockwise for a positive rotation.
            // Its service's rotate instruction computes x cos - y sin and
            // x sin + y cos on a stick whose Y points up, its stick view turns
            // the direction zones clockwise (StickCrossView's RotateTransform),
            // and its radial menu takes the rotation off a clockwise bearing
            // (OverlayMenuVME.CalcAngle). PadForge's Y points down, so a push
            // to the right reads cos on X and -sin on Y: up and to the right.
            var state = Centered();
            state.Axis[0] = 65535;
            float Read(MappingRow row) => row.Sources.Sum(s => SourceCoercion.EvaluateForBipolarAxisTarget(state, s, 0, false, ""));
            Assert.InRange(Read(x), 0.80f, 0.92f);
            Assert.InRange(Read(y), -0.58f, -0.42f);
        }

        [Fact]
        public void ARotatedStick_TurnsCleanly_WhenNoDirectionReadsIt()
        {
            var r = Import(File(hardware: Tuned("""{"sticks":[{"name":"left","rotation":30}]}""")));
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Clean && e.Binding == "Tuning");
            Assert.DoesNotContain(r.Report.Entries, e => e.Status == TranslationStatus.Partial && (e.SourcePath ?? "").Contains("Left stick"));
        }

        [Fact]
        public void ARotatedSticksDirection_SaysTheRotationDoesNotReachIt()
        {
            // A direction reads one raw axis, and reWASD turns its direction
            // zones with the stick.
            var r = Import(File(hardware: Tuned("""{"sticks":[{"name":"left","rotation":30}]}"""),
                masks: "[" + Mask(1, 1, 40) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Partial && e.Binding == "Directions"
                && e.ReasonKey == ImportReasons.ControllerSetting && e.ReasonArgs.Contains("rotation"));
        }

        [Fact]
        public void ARotatedSticksDiagonal_TurnsWithIt()
        {
            // A quarter turn counterclockwise: the push that reads as up and
            // to the right lies down and to the right.
            var r = Import(File(hardware: Tuned("""{"sticks":[{"name":"left","rotation":90}]}"""),
                masks: "[" + Mask(1, 1, 176) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var src = Row(r.Profile, "KbmKey20").Sources[0];
            var state = Centered();
            state.Axis[0] = Right(60);
            state.Axis[1] = Down(60);
            Assert.True(ButtonRead(state, src));
            state.Axis[1] = Up(60);
            Assert.False(ButtonRead(state, src));
            Assert.DoesNotContain(r.Report.Entries, e => e.Binding == "Directions");
        }

        [Fact]
        public void AStickWithZones_KeepsItsCleanTuningLine()
        {
            var r = Import(File(hardware: Tuned("""{"sticks":[{"name":"left","zones":[{"name":"low","x":3000}]}]}""")));
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Clean && e.Binding == "Tuning");
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.ControllerSetting && e.ReasonArgs.Contains("rotation"));
        }

        [Fact]
        public void NinetyDegreeRotation_DropsItsZeroLegs()
        {
            // A quarter turn counterclockwise: right reads up, so X follows Y
            // and Y follows X inverted.
            var p = Profile(File(hardware: Tuned("""{"sticks":[{"name":"left","rotation":90}]}""")));
            var x = Assert.Single(Row(p, "LeftThumbAxisX").Sources);
            Assert.Equal("Gamepad LeftStickY", x.Descriptor);
            Assert.False(x.Invert);
            var y = Assert.Single(Row(p, "LeftThumbAxisY").Sources);
            Assert.Equal("Gamepad LeftStickX", y.Descriptor);
            Assert.True(y.Invert);
        }

        [Fact]
        public void ATateConfigsStick_TurnsTheWayItsDpadDoes()
        {
            // The public sample "Shatter - TATE Landscape 180 Preactivate"
            // (rewasd-profiles-supersmo, reWASD 9.1.0) sends D-pad Up as Right,
            // Right as Down, Down as Left and Left as Up, a quarter turn
            // clockwise, and sets both sticks to -90. The two agree only when a
            // positive rotation turns the stick counterclockwise, so the stick
            // pushed up reads right.
            var p = Profile(File(hardware: Tuned("""{"sticks":[{"name":"left","rotation":-90}]}""")));
            var state = Centered();
            state.Axis[1] = Up(100);
            float x = Row(p, "LeftThumbAxisX").Sources.Sum(s => SourceCoercion.EvaluateForBipolarAxisTarget(state, s, 0, false, ""));
            Assert.InRange(x, 0.9f, 1.0f);
        }

        [Fact]
        public void ARotationPastNinety_LeavesTheStickStraight()
        {
            // reWASD's loader (ConfigJSON_3_0) takes -90 to 90 and leaves the
            // stick straight for anything else.
            var p = Profile(File(hardware: Tuned("""{"sticks":[{"name":"left","rotation":120}]}""")));
            Assert.Equal("Gamepad LeftStickX", Assert.Single(Row(p, "LeftThumbAxisX").Sources).Descriptor);
        }

        [Fact]
        public void Curve_CarriesItsOwnPoints_ToTheDevicesCurveCards()
        {
            var r = Import(File(hardware: Tuned(
                """{"sticks":[{"name":"left","curves":{"points":{"horizontal":[{"id":1,"value":2293,"travel":2531},{"id":2,"value":5570,"travel":4781},{"id":3,"value":9830,"travel":7594},{"id":4,"value":32768,"travel":10125}]}}}]}""")));
            string curves = r.Profile.SlotMappingSets[0].WorkshopCurves;
            Assert.Contains("LeftThumbAxisX=", curves);
            Assert.Contains("LeftThumbAxisY=", curves);
            Assert.Equal(0.0, Row(r.Profile, "LeftThumbAxisX").Sources[0].ParamCurveExponent);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void TriggerZones_ReachTheGame_OnlyWithAffectHardware()
        {
            var plain = Row(Profile(File(hardware: Tuned("""{"triggers":[{"name":"left","zones":[{"name":"low","x":3277},{"name":"high","x":29490}]}]}"""))), "LeftTrigger").Sources[0];
            Assert.Equal(0, plain.ParamStickDeadZoneShape);
            Assert.Equal(0.0, plain.ParamStickDeadZoneInner);

            var r = Import(File(hardware: Tuned("""{"triggers":[{"name":"left","affectHardware":true,"zones":[{"name":"low","x":3277},{"name":"high","x":29490}]}]}""")));
            var src = Row(r.Profile, "LeftTrigger").Sources[0];
            Assert.Equal("Gamepad LeftTrigger", src.Descriptor);
            Assert.Equal(1, src.ParamStickDeadZoneShape);
            Assert.InRange(src.ParamStickDeadZoneInner, 0.09, 0.11);
            // The high zone places zone mappings, not the trigger's top.
            Assert.Equal(new MappingSource().ParamRangeOuter, src.ParamRangeOuter);
            Assert.DoesNotContain(r.Report.Entries, e => e.Status != TranslationStatus.Clean);
        }

        [Fact]
        public void APresetFile_ReadsItsOneLayerAsTheConfig()
        {
            // reWASD's presets keep every mapping on one layer that nothing
            // opens, for a user to apply to a layer of a config.
            var r = Import(File(shifts: """[{"id":1,"type":"default","description":"Gamepad to Keyboard and Mouse"}]""",
                masks: "[" + Mask(1, 1, 1) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]", shift: 1) + "]"));
            Assert.Equal("Gamepad ButtonA", Row(r.Profile, "KbmKey20").Sources[0].Descriptor);
            Assert.Empty(r.Profile.SlotMappingSets[0].ShiftActivators);
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.LayerNoInput);
        }

        [Fact]
        public void ALayerSomethingOpens_StaysALayer()
        {
            var r = Import(File(shifts: """[{"id":1,"type":"default"}]""",
                masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 1, 5) + "]",
                mappings: "[" + Jump(2, 1) + "," + Map(1, "[" + Key(57) + "]", shift: 1) + "]"));
            Assert.NotNull(Row(r.Profile, "KbmKey20", "Shift1"));
            Assert.Null(Row(r.Profile, "KbmKey20"));
        }

        [Fact]
        public void ARadialLayerNothingOpens_IsLeftOut()
        {
            // Every config carries one, and reWASD opens it only on a jump.
            var r = Import(File(shifts: """[{"id":2,"type":"radialMenu"}]""",
                masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 1, 40) + "]",
                commands: """[{"id":1,"type":134,"name":"OVERLAY_MENU_UP"}]""",
                mappings: "[" + Map(1, "[" + Key(57) + "]") + "," + Map(2, """[{"command":{"id":1}}]""", shift: 2) + "]"));
            Assert.All(r.Profile.SlotMappingSets.SelectMany(s => s.Rows), row => Assert.NotEqual("Shift2", row.LayerMask));
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.LayerNoInput);
            Assert.NotNull(Row(r.Profile, "KbmKey20"));
        }

        [Fact]
        public void PerLayerTuning_GivesTheLayerItsOwnStickRows()
        {
            var p = Profile(File(shifts: OneLayer, hardware:
                """[{"id":1,"name":"gamepad","convertToVirtual":true,"shifts":[{"shiftId":1,"sticks":[{"name":"left","rotation":90}]}]}]"""));
            Assert.Equal("Gamepad LeftStickX", Assert.Single(Row(p, "LeftThumbAxisX").Sources).Descriptor);
            Assert.Equal("Gamepad LeftStickY", Assert.Single(Row(p, "LeftThumbAxisX", "Shift1").Sources).Descriptor);
        }

        [Theory]
        [InlineData("zoneLow", 30, 66)]
        [InlineData("zoneMed", 66, 85)]
        [InlineData("zoneHigh", 85, 0)]
        public void ZoneActivator_ReadsInsideThatZone(string type, int low, int high)
        {
            var r = Import(File(masks: "[" + Mask(1, 1, 40) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]", type: type) + "]"));
            var src = Row(r.Profile, "KbmKey20").Sources[0];
            Assert.Equal(low, src.DeadZone);
            Assert.Equal(high == 0 ? "" : BoundedReads.Between(low, high, true, true, "Gamepad LeftStickY"), src.GateDescriptor);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);

            // Up past the zone's edge reads, and past the next zone's edge it
            // lets go: reWASD's zone mapping is released when the stick
            // leaves the zone.
            var state = Centered();
            state.Axis[1] = Up(low + 5);
            Assert.True(ButtonRead(state, src));
            state.Axis[1] = Up(high == 0 ? 100 : high + 5);
            Assert.Equal(high == 0, ButtonRead(state, src));
        }

        [Fact]
        public void LowZoneInput_IsARingBand_ThatLetsGoAtTheMediumZone()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 37) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var src = Row(p, "KbmKey20").Sources[0];
            Assert.Equal("Gamepad LeftStickRing", src.Descriptor);
            Assert.Equal(30, src.DeadZone);
            Assert.Equal(BoundedReads.Between(30, 66, false, false, "Gamepad LeftStickRing"), src.GateDescriptor);
            var state = Centered();
            state.Axis[0] = Right(40);
            Assert.True(ButtonRead(state, src));
            state.Axis[0] = Right(80);
            Assert.False(ButtonRead(state, src));
        }

        [Fact]
        public void MediumTriggerZone_LetsGoAtTheHighZone()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 53) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var src = Row(p, "KbmKey20").Sources[0];
            Assert.Equal("Gamepad LeftTrigger", src.Descriptor);
            var state = new CustomInputState();
            state.Axis[2] = 65535 * 70 / 100;
            Assert.True(ButtonRead(state, src));
            state.Axis[2] = 65535;
            Assert.False(ButtonRead(state, src));
        }

        [Fact]
        public void StickZoneInput_IsARingAtThatRadius()
        {
            var p = Profile(File(hardware: """[{"id":1,"name":"gamepad","convertToVirtual":true,"default":{"sticks":[{"name":"left","zones":[{"name":"high","x":29490}]}]}}]""",
                masks: "[" + Mask(1, 1, 39) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var src = Row(p, "KbmKey20").Sources[0];
            Assert.Equal("Gamepad LeftStickRing", src.Descriptor);
            Assert.Equal(90, src.DeadZone);
        }

        [Fact]
        public void TriggerAsButton_PressesEarly()
        {
            var state = new CustomInputState();
            var p = Profile(File(masks: "[" + Mask(1, 1, 51) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var src = Row(p, "KbmKey20").Sources[0];
            state.Axis[2] = 65535 * 20 / 100;
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, src, 50, 0, ""));
        }

        // ── Radial menus ────────────────────────────────────────────────

        [Fact]
        public void RadialMenu_LandsOnItsLayerWithItsItems()
        {
            var json = File(
                shifts: """[{"id":2,"type":"radialMenu"}]""",
                masks: "[" + Mask(1, 1, 6) + "," + Mask(2, 1, 40) + ","
                    + """{"id":3,"radialMenuSet":[{"circleId":1,"sectorId":1}]},{"id":4,"radialMenuSet":[{"circleId":1,"sectorId":2}]}""" + "]",
                commands: """[{"id":1,"type":134,"name":"OVERLAY_MENU_UP"}]""",
                mappings: "["
                    + Jump(1, 2) + ","
                    + Map(2, """[{"command":{"id":1}}]""", shift: 2) + ","
                    + Map(3, "[" + Key(57) + "]", shift: 2) + ","
                    + Map(4, "[" + Key(28) + "]", shift: 2) + "]",
                extra: ""","radialMenuCircles":[{"id":1,"sectors":[1,2]}],"radialMenuSectors":[{"parentCircleId":1,"id":1,"description":"Jump"},{"parentCircleId":1,"id":2,"description":"Run"}]""");
            var r = Import(json);
            var menu = Assert.Single(r.Profile.SlotMappingSets[0].Menus);
            Assert.Equal(MenuKind.Radial, menu.Kind);
            Assert.Equal("Shift2", menu.LayerMask);
            Assert.Equal("Gamepad LeftStick", menu.HostDescriptor);
            Assert.Equal(2, menu.CellCount);
            Assert.Equal("Jump", menu.Items[0].Label);
            Assert.Equal(1, menu.Items[0].Index);
            Assert.Equal(MenuFireType.TouchRelease, menu.FireType);

            // A sector fires as its layer ends, so its row lives on Base.
            Assert.Equal("Menu 1 Item 1", Row(r.Profile, "KbmKey20").Sources[0].Descriptor);
            Assert.Equal("Menu 1 Item 2", Row(r.Profile, "KbmKey0D").Sources[0].Descriptor);
            // The file maps no Apply, so leaving the layer is its only commit,
            // and that is the menu's own.
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.RadialMenu);

            // Navigation is the menu's own job, and it must not read as lost.
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.UnknownCommand);
        }

        [Fact]
        public void RadialSubMenu_JoinsTheRing_AndSaysSo()
        {
            var json = File(
                shifts: """[{"id":2,"type":"radialMenu"}]""",
                masks: "[" + Mask(9, 1, 6) + "," + """{"id":3,"radialMenuSet":[{"circleId":2,"sectorId":3}]}]""",
                mappings: "[" + Jump(9, 2) + "," + Map(3, "[" + Key(57) + "]", shift: 2) + "]",
                extra: ""","radialMenuCircles":[{"id":1,"sectors":[1,2]},{"id":2,"parentSectorId":1,"sectors":[3]}],"radialMenuSectors":[{"parentCircleId":1,"id":1,"childCircleId":2},{"parentCircleId":1,"id":2},{"parentCircleId":2,"id":3}]""");
            var r = Import(json);
            var menu = Assert.Single(r.Profile.SlotMappingSets.First(s => s.Menus.Count > 0).Menus);
            Assert.Equal(2, menu.CellCount);
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.RadialFlattened);
        }

        // ── Lightbar ────────────────────────────────────────────────────

        [Fact]
        public void Led_IsAHeldColorPerLayer_BaseFirst()
        {
            var r = Import(File(shifts: OneLayer, config:
                """{"appName":"x","led":{"default":{"mode":"solid","colors":[[1,2,3]]},"shifts":[{"shiftId":1,"mode":"solid","colors":[[9,8,7]]}]}}"""));
            var macros = r.Profile.Macros;
            Assert.Equal(2, macros.Length);
            Assert.All(macros, m => Assert.Equal(MacroTriggerMode.Always, m.TriggerMode));
            Assert.Equal("Base", macros[0].LayerMask);
            Assert.Equal("Shift1", macros[1].LayerMask);
            var a = Only(macros[0]);
            Assert.Equal(MacroActionType.LightbarColor, a.Type);
            Assert.Equal(MacroLightbarHoldMode.Reactive, a.LightbarHoldMode);
            Assert.True(a.LightbarHoldMs > 0);
            // A layer's color alone must not stop it inheriting Base.
            Assert.True(r.Profile.SlotMappingSets[0].ShiftActivators.Single().InheritUnmapped);
            Assert.Equal((byte)1, a.LightbarR);
            Assert.Equal((byte)3, a.LightbarB);
            Assert.Equal((byte)9, Only(macros[1]).LightbarR);
            // The one line left says nothing in the file opens the layer.
            Assert.All(r.Report.Entries.Where(e => e.Status != TranslationStatus.Clean),
                e => Assert.Equal(ImportReasons.LayerNoInput, e.ReasonKey));
        }

        [Fact]
        public void LedPatternOnALayer_StopsItInheritingBasesColor()
        {
            // Base writes its color every frame, so a pattern under it would
            // only show its first step.
            var r = Import(File(shifts: OneLayer, config:
                """{"appName":"x","led":{"default":{"mode":"solid","colors":[[1,2,3]]},"shifts":[{"shiftId":1,"mode":"shimmer","colors":[[200,100,40]]}]}}"""));
            Assert.False(r.Profile.SlotMappingSets[0].ShiftActivators.Single().InheritUnmapped);
            var layer = r.Profile.Macros.Single(m => m.LayerMask == "Shift1");
            Assert.Equal(4, layer.Actions.Length);
            Assert.Equal((byte)200, layer.Actions[0].LightbarR);
            Assert.Equal((byte)150, layer.Actions[2].LightbarR);
            Assert.All(r.Report.Entries.Where(e => e.Status != TranslationStatus.Clean),
                e => Assert.Equal(ImportReasons.LayerNoInput, e.ReasonKey));
        }

        [Theory]
        [InlineData("rainbow", 72)]
        [InlineData("breath", 6)]
        [InlineData("heartbeat", 4)]
        [InlineData("stroboscope", 4)]
        [InlineData("shimmer", 4)]
        public void LedModes_PlayAsALoopingPattern(string mode, int actions)
        {
            var r = Import(File(config: "{\"appName\":\"x\",\"led\":{\"default\":{\"mode\":\"" + mode + "\",\"colors\":[[255,0,0]]}}}"));
            var m = Assert.Single(r.Profile.Macros);
            Assert.Equal(MacroTriggerMode.Always, m.TriggerMode);
            Assert.Equal(actions, m.Actions.Length);
            Assert.Equal(MacroActionType.LightbarColor, m.Actions[0].Type);
            Assert.Equal(MacroActionType.Delay, m.Actions[1].Type);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void RadialMenuWithApplyMapped_SaysWhereItCommits()
        {
            var json = File(
                shifts: """[{"id":2,"type":"radialMenu"}]""",
                masks: "[" + Mask(1, 1, 6) + "," + Mask(2, 1, 40) + "," + Mask(5, 1, 1) + ","
                    + """{"id":3,"radialMenuSet":[{"circleId":1,"sectorId":1}]}""" + "]",
                commands: """[{"id":1,"type":134,"name":"OVERLAY_MENU_UP"},{"id":2,"type":132,"name":"OVERLAY_MENU_APPLY"}]""",
                mappings: "["
                    + Jump(1, 2) + ","
                    + Map(2, """[{"command":{"id":1}}]""", shift: 2) + ","
                    + Map(5, """[{"command":{"id":2}}]""", shift: 2) + ","
                    + Map(3, "[" + Key(57) + "]", shift: 2) + "]",
                extra: ""","radialMenuCircles":[{"id":1,"sectors":[1]}],"radialMenuSectors":[{"parentCircleId":1,"id":1,"description":"Jump"}]""");
            var r = Import(json);
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.RadialMenu);
        }

        // ── What does not come across says so ───────────────────────────

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [Fact]
        public void MouseRegion_IsAHeldClamp_OnTheFilesOwnRectangle()
        {
            var r = Import(File(masks: "[" + Mask(1, 1, 1) + "]", mappings: "["
                + Map(1, "", more: ""","mouseRegion":{"x1":0,"y1":0,"x2":800,"y2":600}""") + "]"));
            Assert.Equal(2, r.Profile.Macros.Length);
            var press = Only(r.Profile.Macros[0]);
            Assert.Equal(MacroActionType.MouseLimitRegion, press.Type);
            Assert.Equal(MacroLatchDirection.On, press.LatchDirection);
            Assert.Equal(MacroLatchDirection.Off, Only(r.Profile.Macros[1]).LatchDirection);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);

            // The clamp holds the cursor in the file's top-left 800 x 600.
            int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
            if (w < 800 || h < 600) return;
            var far = CursorControlService.ClampPoint(5000, 5000, 0, 0, w, h, CursorClampMode.XAndY,
                press.CursorClampInsetX, press.CursorClampInsetY, press.CursorClampOffsetX, press.CursorClampOffsetY);
            Assert.InRange(far.X, 799, 801);
            Assert.InRange(far.Y, 599, 601);
            var near = CursorControlService.ClampPoint(-50, -50, 0, 0, w, h, CursorClampMode.XAndY,
                press.CursorClampInsetX, press.CursorClampInsetY, press.CursorClampOffsetX, press.CursorClampOffsetY);
            Assert.InRange(near.X, 0, 1);
            Assert.InRange(near.Y, 0, 1);
        }

        [Fact]
        public void CrosshairsAndCrosshairMasks_AreSkippedWithAReason()
        {
            var r = Import(File(masks: """[{"id":1,"crosshairSet":[{"crosshairGuid":"x","layer":0}]}]""",
                mappings: "[" + Map(1, "[" + Key(57) + "]") + "]",
                extra: ""","crosshairs":[{"id":1}]"""));
            Assert.Equal(2, r.Report.Entries.Count(e => e.ReasonKey == ImportReasons.Crosshair));
        }

        [Fact]
        public void ALayerThatLosesInheritance_KeepsItsOwnColor_NotBases()
        {
            var r = Import(File(shifts: OneLayer, masks: "[" + Mask(1, 1, 1) + "," + Mask(3, 1, 5) + "]",
                config: """{"appName":"x","led":{"default":{"mode":"solid","colors":[[1,2,3]]},"shifts":[{"shiftId":1,"mode":"solid","colors":[[9,8,7]]}]}}""",
                mappings: "[" + Jump(3, 1) + ","
                    + Map(1, "[" + Key(57) + "]", mode: "onetime") + ","
                    + Map(1, "[" + Key(15) + "]", mode: "onetime", shift: 1) + "]"));
            var onLayer = r.Profile.Macros.Where(m => m.LayerMask == "Shift1" && m.TriggerMode == MacroTriggerMode.Always).ToList();
            Assert.Equal((byte)9, Only(Assert.Single(onLayer)).LightbarR);
        }

        [Fact]
        public void ALayerWithNoColorOfItsOwn_KeepsBases_WhenItStopsInheriting()
        {
            var r = Import(File(shifts: OneLayer, masks: "[" + Mask(1, 1, 1) + "," + Mask(3, 1, 5) + "]",
                config: """{"appName":"x","led":{"default":{"mode":"solid","colors":[[1,2,3]]}}}""",
                mappings: "[" + Jump(3, 1) + ","
                    + Map(1, "[" + Key(57) + "]", mode: "onetime") + ","
                    + Map(1, "[" + Key(15) + "]", mode: "onetime", shift: 1) + "]"));
            var onLayer = r.Profile.Macros.Where(m => m.LayerMask == "Shift1" && m.TriggerMode == MacroTriggerMode.Always).ToList();
            Assert.Equal((byte)1, Only(Assert.Single(onLayer)).LightbarR);
        }

        [Fact]
        public void MouseStickRemap_ReadsAllMice()
        {
            var r = Import(File(mappings: """[{"condition":{"hardware":{"deviceId":23,"stick":"mouse","axis":"x"}},"hardware":{"stick":"right","axis":"x"}}]"""));
            var src = Row(r.Profile, "RightThumbAxisX").Sources.First(s => s.DeviceGuid == AllMice);
            Assert.Equal("Axis 0", src.Descriptor);
            Assert.True(r.ReadsMouse);
        }

        [Fact]
        public void MouseStickUnmap_SaysMovementIsNotHeldBack()
        {
            var r = Import(File(mappings: """[{"condition":{"hardware":{"deviceId":23,"stick":"mouse","axis":"x"}},"hardware":{"unmap":true}}]"""));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.MouseMovement);
        }

        [Fact]
        public void UnmappedDirection_KeepsTheOtherHalf_AtFullReach()
        {
            // The axis range sizes direction zones, so the half that stays
            // reaches the game whole.
            var p = Profile(File(hardware: """[{"id":1,"name":"gamepad","convertToVirtual":true,"default":{"sticks":[{"name":"left","sensitivity":{"x":16384,"y":16384}}]}}]""",
                mappings: """[{"condition":{"hardware":{"deviceId":1,"buttonId":40}},"hardware":{"unmap":true}}]"""));
            var src = Assert.Single(Row(p, "LeftThumbAxisY").Sources);
            Assert.True(src.HalfAxis);
            Assert.Equal(1.0, src.Sensitivity);
        }

        [Fact]
        public void TouchpadClickOutput_IsARow_AndNamedInAMacro()
        {
            var row = Profile(File(virtualType: "ds4", masks: "[" + Mask(1, 11, 30) + "]",
                mappings: "[" + Map(1, """[{"gamepad":{"id":1,"buttonId":99}}]""") + "]"));
            Assert.Contains(Row(row, "TouchpadClick").Sources, s => s.Descriptor == "Button 65");

            var macro = Import(File(virtualType: "ds4", masks: "[" + Mask(1, 11, 30) + "]",
                mappings: "[" + Map(1, """[{"gamepad":{"id":1,"buttonId":99}}]""", mode: "onetime") + "]"));
            // The DualShock 4 has the button. A macro cannot press it.
            Assert.Contains(macro.Report.Entries, e => e.ReasonKey == ImportReasons.MacroCannotPress);
            Assert.Empty(macro.Profile.Macros);
        }

        [Fact]
        public void WhatAFileSetsAndNoRowReads_IsNamed()
        {
            var r = Import(File(hardware: Tuned("""{"sticks":[{"name":"left","nativeMode":true,"mouse":{"sensitivity":10}}],"triggers":[{"name":"left","adaptiveMode":3}],"touchpads":[{"id":2,"mode":"analog","springMode":true}],"vibration":[{"motor":{"name":"left"}}]}""")));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.MoveSpeed);
            foreach (string name in new[] { "nativeMode", "adaptiveMode", "springMode", "vibration" })
                Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.ControllerSetting && e.ReasonArgs.Any(a => a.Contains(name)));
        }

        [Fact]
        public void ATouchpadsDefaults_SayNothing()
        {
            var r = Import(File(hardware: Tuned("""{"touchpads":[{"id":2,"mode":"analog","trackballFriction":50}]}""")));
            Assert.Equal(0, r.Report.PartialCount);
        }

        [Fact]
        public void ARepeatingSequence_ClearsWhatItHoldsOnRelease()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]", mappings: "["
                + Map(1, "[" + Key(30, "down") + """,{"pause":{"value":40}},""" + Key(30, "up") + "]", mode: "repeat") + "]"));
            Assert.Equal(2, p.Macros.Length);
            var run = p.Macros[0];
            var twin = p.Macros[1];
            Assert.Equal(MacroTriggerMode.WhileHeld, run.TriggerMode);
            Assert.Equal(MacroTriggerMode.OnRelease, twin.TriggerMode);
            Assert.Equal(run.PairId, twin.PairId);
            Assert.NotEqual(0, run.PairId);
            Assert.Equal(MacroLatchDirection.Off, Only(twin).LatchDirection);
        }

        [Fact]
        public void AToggledSequence_ClearsWhatItHolds_OnThePressThatStopsIt()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]", mappings: "["
                + Map(1, "[" + Key(30, "down") + """,{"pause":{"value":40}},""" + Key(30, "up") + "]", mode: "toggle") + "]"));
            Assert.Equal(2, p.Macros.Length);
            var twin = p.Macros[0];
            Assert.Equal(MacroTriggerMode.OnPress, twin.TriggerMode);
            Assert.Equal(MacroLatchDirection.Off, Only(twin).LatchDirection);
            Assert.Equal(MacroTriggerMode.Toggle, p.Macros[1].TriggerMode);
            Assert.Equal(twin.PairId, p.Macros[1].PairId);
        }

        [Fact]
        public void ALayersOwnTriggerZones_WriteNoStickRows()
        {
            var p = Profile(File(shifts: OneLayer, hardware: """[{"id":1,"name":"gamepad","convertToVirtual":true,"shifts":[{"shiftId":1,"triggers":[{"name":"left","zones":[{"name":"low","x":3277}]}]}]}]"""));
            Assert.Null(Row(p, "RightThumbAxisX", "Shift1"));
            Assert.Null(Row(p, "RightThumbAxisY", "Shift1"));
            Assert.Equal("Gamepad LeftTrigger", Assert.Single(Row(p, "LeftTrigger", "Shift1").Sources).Descriptor);
        }

        [Fact]
        public void TwoMappingsOfOneInputToOneOutput_AreOneSource()
        {
            var p = Profile(File(hardware: """[{"id":1,"name":"gamepad"},{"id":2,"name":"gamepad"}]""",
                masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 2, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]") + "," + Map(2, "[" + Key(57) + "]") + "]"));
            Assert.Single(Row(p, "KbmKey20").Sources);
        }

        [Fact]
        public void AOneShotJumpBesideADoublePress_WaitsToBeSure()
        {
            var r = Import(File(shifts: OneLayer, masks: "[" + Mask(1, 1, 1) + "]", mappings: "["
                + Map(1, "", mode: "onetime", more: ""","jumpToLayer":{"layer":1}""") + ","
                + Map(1, "[" + Key(57) + "]", type: "double", mode: "onetime") + "]"));
            var jump = r.Profile.Macros.First(m => m.Actions.Any(a => a.Type == MacroActionType.SwitchLayer));
            Assert.Equal(MacroTriggerMode.SinglePress, jump.TriggerMode);
        }

        [Fact]
        public void MouseStickUnmap_WithNoDeviceNamed_IsStillTheMouse()
        {
            var r = Import(File(mappings: """[{"condition":{"hardware":{"stick":"mouse","axis":"x"}},"hardware":{"unmap":true}}]"""));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.MouseMovement);
        }

        [Fact]
        public void AHalfUnmapOnARotatedStick_SaysTheRotationDidNotComeAcross()
        {
            var r = Import(File(hardware: """[{"id":1,"name":"gamepad","convertToVirtual":true,"default":{"sticks":[{"name":"left","rotation":30}]}}]""",
                mappings: """[{"condition":{"hardware":{"deviceId":1,"buttonId":40}},"hardware":{"unmap":true}}]"""));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.ControllerSetting && e.ReasonArgs.Contains("rotation"));
        }

        [Fact]
        public void AStepThisDoesNotKnow_IsNamed()
        {
            var r = Import(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, """[{"teleport":{"id":1}}]""") + "]"));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.UnknownOutput && e.ReasonArgs.Contains("teleport"));
        }

        [Fact]
        public void ACircleThatNamesItself_EndsAndKeepsTheOtherSectors()
        {
            var r = Import(File(shifts: """[{"id":2,"type":"radialMenu"}]""",
                masks: "[" + Mask(9, 1, 6) + "]", mappings: "[" + Jump(9, 2) + "]",
                extra: ""","radialMenuCircles":[{"id":1,"sectors":[1,2]}],"radialMenuSectors":[{"id":1,"childCircleId":1},{"id":2},{"id":2}]"""));
            Assert.False(r.Refused);
            Assert.Equal(1, r.Profile.SlotMappingSets[0].Menus.Single().CellCount);
        }

        [Fact]
        public void AChainOfCirclesDeeperThanAnyMenu_Ends()
        {
            var circles = string.Join(",", Enumerable.Range(1, 100000).Select(i => "{\"id\":" + i + ",\"sectors\":[" + i + "]}"));
            var sectors = string.Join(",", Enumerable.Range(1, 100000).Select(i => "{\"id\":" + i + ",\"childCircleId\":" + (i + 1) + "}"));
            var r = Import(File(extra: ",\"radialMenuCircles\":[" + circles + "],\"radialMenuSectors\":[" + sectors + "]"));
            Assert.Equal(ImportReasons.FileTooLarge, r.RefusedKey);

            circles = string.Join(",", Enumerable.Range(1, 2000).Select(i => "{\"id\":" + i + ",\"sectors\":[" + i + "]}"));
            sectors = string.Join(",", Enumerable.Range(1, 2000).Select(i => "{\"id\":" + i + ",\"childCircleId\":" + (i + 1) + "}"));
            Assert.False(Import(File(extra: ",\"radialMenuCircles\":[" + circles + "],\"radialMenuSectors\":[" + sectors + "]")).Refused);
        }

        [Fact]
        public void TooManyShifts_AreRefused()
        {
            var shifts = "[" + string.Join(",", Enumerable.Range(1, 65).Select(i => "{\"id\":" + i + "}")) + "]";
            Assert.Equal(ImportReasons.FileTooLarge, Import(File(shifts: shifts)).RefusedKey);
        }

        /// <summary>Every value in a file that uses every part of the format,
        /// replaced by each JSON kind in turn. A wrong kind can lose a line and
        /// must never throw.</summary>
        [Fact]
        public void AnyValueOfTheWrongKind_NeverThrows()
        {
            string rich = File(
                shifts: """[{"id":1,"type":"default","description":"L","set":[{"deviceId":1,"buttonId":5}],"unheritableMasks":[1]},{"id":2,"type":"radialMenu"}]""",
                hardware: """[{"id":1,"name":"gamepad","convertToVirtual":true,"default":{"sticks":[{"name":"left","rotation":10,"sensitivity":{"x":100,"y":200},"zones":[{"name":"low","x":3000}],"curves":{"points":{"horizontal":[{"travel":10,"value":5},{"travel":50,"value":30}]}}},{"name":"third","x":1}],"triggers":[{"name":"left","zones":[{"name":"high","x":30000}]}],"touchpads":[{"id":2,"mode":"analog","springMode":true}],"vibration":[{"motor":{"name":"left"}}],"analogButtons":[{"name":"a"}]},"shifts":[{"shiftId":1,"sticks":[{"name":"right"}]}]},{"id":11,"name":"keyboard"},{"id":23,"name":"mouse","default":{"scrolls":[1]}}]""",
                commands: """[{"id":1,"type":1000,"name":"L","params":{"path":"x"}},{"id":2,"type":6},{"id":3,"type":134,"description":"r"}]""",
                masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 11, 30) + "," + Mask(3, 23, 1) + """,{"id":4,"radialMenuSet":[{"circleId":1,"sectorId":1}]},{"id":5,"crosshairSet":[1]},{"id":6,"set":[{"deviceId":1,"buttonId":40},{"deviceId":1,"buttonId":1}]}""" + "]",
                mappings: "[" + Map(1, "[" + Key(57) + """,{"pause":{"value":40}},{"mouse":{"direction":"left","value":3}},{"mouse":{"wheel":"up"}},{"gamepad":{"id":1,"buttonId":40,"value":100,"absolute":false}},{"rumble":{"duration":5,"amplitude":{"motor":{"left":1,"right":2},"trigger":{"left":1,"right":2}}}},{"break":{}},{"command":{"id":1}},{"command":{"id":2}},{"command":{"id":3}}]""", mode: "toggle", activatorParams: """{"longwaittime":100,"macro":true}""") + ","
                    + Map(2, "[" + Key(30, "down") + "]", type: "long", mode: "repeat") + ","
                    + Map(3, "", mode: "toggle", more: ""","jumpToLayer":{"layer":1,"delay":5,"postpone":true},"mouseRegion":{"x1":0,"y1":0,"x2":100,"y2":100}""") + ","
                    + Map(4, "[" + Key(57) + "]") + "," + Map(6, "[" + Key(57) + "]", type: "zoneLow") + ","
                    + """{"condition":{"hardware":{"deviceId":1,"stick":"left","axis":"x"}},"hardware":{"stick":"right","axis":"y","inverted":true}},{"condition":{"hardware":{"deviceId":1,"trigger":"left"}},"hardware":{"trigger":"right"}},{"condition":{"hardware":{"deviceId":1,"buttonId":40}},"hardware":{"unmap":true}},{"condition":{"hardware":{"deviceId":1,"buttonId":2}},"hardware":{"buttonId":3}},{"condition":{"hardware":{"deviceId":11,"buttonId":30}},"hardware":{"buttonId":31}}""" + "]",
                config: """{"appName":"x","comment":"c","processNames":["a.exe",1],"led":{"default":{"mode":"shimmer","colors":[[1,2,3]]},"shifts":[{"shiftId":1,"mode":"rainbow"}]}}""",
                extra: ""","radialMenuCircles":[{"id":1,"sectors":[1,2]},{"id":2,"parentSectorId":2,"sectors":[3]}],"radialMenuSectors":[{"id":1,"description":"a"},{"id":2,"childCircleId":2},{"id":3}],"crosshairs":[{"id":1}]""");
            Assert.False(ForeignConfigImporter.TranslateCore(Encoding.UTF8.GetBytes(rich), "x.rewasd").Refused);

            var wrongKinds = new System.Text.Json.Nodes.JsonNode[] { null, 1.5, "x", new System.Text.Json.Nodes.JsonArray(), new System.Text.Json.Nodes.JsonObject(), true, 1e300, -1 };
            var root = System.Text.Json.Nodes.JsonNode.Parse(rich);
            var paths = new List<List<object>>();
            void Walk(System.Text.Json.Nodes.JsonNode node, List<object> at)
            {
                if (at.Count > 0) paths.Add(at);
                if (node is System.Text.Json.Nodes.JsonObject o)
                    foreach (var kv in o) Walk(kv.Value, at.Append(kv.Key).ToList());
                else if (node is System.Text.Json.Nodes.JsonArray a)
                    for (int i = 0; i < a.Count; i++) Walk(a[i], at.Append(i).ToList());
            }
            Walk(root, new List<object>());
            Assert.True(paths.Count > 150, paths.Count.ToString());

            foreach (var at in paths)
            {
                foreach (var wrong in wrongKinds)
                {
                    var copy = System.Text.Json.Nodes.JsonNode.Parse(rich);
                    var parent = copy;
                    for (int i = 0; i < at.Count - 1; i++)
                        parent = at[i] is string k ? parent[k] : parent[(int)at[i]];
                    var value = wrong?.DeepClone();
                    if (at[^1] is string key) parent[key] = value;
                    else parent[(int)at[^1]] = value;
                    var bytes = Encoding.UTF8.GetBytes(copy.ToJsonString());
                    var ex = Record.Exception(() => ForeignConfigImporter.TranslateCore(bytes, "x.rewasd"));
                    Assert.True(ex == null, string.Join("/", at) + " = " + (value?.ToJsonString() ?? "null") + ": " + ex);
                }
            }
        }

        [Fact]
        public void AnActivatorsWait_RunsFirst_AndMakesItAMacro()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]", activatorParams: """{"expert":true,"delay":120}""") + "]"));
            Assert.Null(Row(p, "KbmKey20"));
            var press = p.Macros[0];
            Assert.Equal(MacroActionType.Delay, press.Actions[0].Type);
            Assert.Equal(120, press.Actions[0].DurationMs);
            Assert.Equal(MacroLatchDirection.On, press.Actions[1].LatchDirection);
        }

        [Theory]
        [InlineData("""{"appName":"x","minTimeUnit":10}""", true)]
        [InlineData("""{"appName":"x","minTimeUnit":1}""", false)]
        public void ATimeUnitThatIsNotOneMs_IsNamed(string config, bool named)
        {
            var r = Import(File(config: config));
            Assert.Equal(named, r.Report.Entries.Any(e => e.ReasonKey == ImportReasons.ControllerSetting && e.ReasonArgs.Contains("minTimeUnit")));
        }

        [Fact]
        public void HidingThePhysicalController_SaysWhereToTurnItOn()
        {
            // PadForge hides a device only once its user confirms it.
            var r = Import(File(hardware: """[{"id":1,"name":"gamepad","hidePhysicalGamepad":true}]"""));
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Partial && e.ReasonKey == ImportReasons.HideController);
        }

        [Fact]
        public void TheFileDialogTakesCustomPlaces()
            => Assert.NotNull(new Microsoft.Win32.OpenFileDialog().CustomPlaces);

        [Fact]
        public void AnalogKeyActuation_IsSkippedWithAReason()
        {
            var r = Import(File(hardware: Tuned("""{"analogButtons":[{"name":"w"}]}""")));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.AnalogActuation);
        }


        private static string WithVirtualPad(string pad, string mappings = "[]", string shifts = "[]", string masks = "[]")
            => "{\"schemaVersion\":\"3.0\",\"config\":{\"appName\":\"x\"},\"devices\":{\"hardware\":" + PadAndKbm
             + ",\"virtual\":{\"gamepads\":[" + pad + "]}},\"shifts\":" + shifts + ",\"masks\":" + masks
             + ",\"mappings\":" + mappings + "}";

        [Fact]
        public void UdpServer_TurnsOnTheMotionServer_AndAnAdapterIsNamed()
        {
            // reWASD's Use for UDP server sends the pad's motion to emulators
            // over cemuhook, the DSU server a PadForge profile turns on as it
            // loads. An external device is an adapter to a console or a PC.
            var r = Import(WithVirtualPad("""{"id":1,"type":"ds4","udp":true,"external":true,"default":{"sticks":[{"name":"left","jitter":true}]}}"""));
            Assert.True(r.Profile.EnableDsuMotionServer);
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Clean && e.Binding == "UDP server");
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Partial && e.ReasonKey == ImportReasons.RemoteTarget);
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.JitterShaking);
        }

        [Fact]
        public void UdpAndExternal_SetFalse_ChangeNothing()
        {
            var r = Import(WithVirtualPad("""{"id":1,"type":"ds4","udp":false,"external":false}"""));
            Assert.False(r.Profile.EnableDsuMotionServer);
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.RemoteTarget);
        }

        [Fact]
        public void VirtualStickRange_IsAFloorAndAScale_OnEveryLayerThatSetsOne()
        {
            // Base starts the right stick at 10648 of 32767. Layer 1 tops the
            // left stick at 17918, a walk, and starts the right at 7165.
            string pad = """{"id":1,"type":"360","default":{"sticks":[{"name":"right","initialDeflection":10648}]},"shifts":[{"shiftId":1,"sticks":[{"name":"left","maxDeflection":17918},{"name":"right","initialDeflection":7165}]}]}""";
            string mappings = "[" + Jump(3, 1) + ","
                + """{"condition":{"hardware":{"deviceId":23,"stick":"mouse","axis":"x"}},"hardware":{"stick":"right","axis":"x"}},"""
                + Map(1, "[{\"gamepad\":{\"id\":1,\"buttonId\":40}}]") + "]";
            var r = Import(WithVirtualPad(pad, mappings, OneLayer, "[" + Mask(1, 11, 17) + "," + Mask(3, 1, 5) + "]"));
            var p = r.Profile;

            // The range is the virtual stick's own, so every input on that
            // stick takes it: the mouse and the pad's own stick alike.
            Assert.Contains(Row(p, "RightThumbAxisX").Sources, s => s.DeviceGuid == ImportBuilder.AllMiceGuid);
            Assert.All(Row(p, "RightThumbAxisX").Sources, s => Assert.Equal(10648 / 32767.0, s.ParamAntiDeadzone, 6));
            Assert.Contains(Row(p, "RightThumbAxisX", "Shift1").Sources, s => s.DeviceGuid == ImportBuilder.AllMiceGuid);
            Assert.All(Row(p, "RightThumbAxisX", "Shift1").Sources, s => Assert.Equal(7165 / 32767.0, s.ParamAntiDeadzone, 6));
            // The walk layer's own entry leaves the floor at reWASD's default.
            Assert.All(Row(p, "LeftThumbAxisY", "Shift1").Sources, s => Assert.Equal(0.0, s.ParamAntiDeadzone));

            var walk = p.Macros.Where(m => m.LayerMask == "Shift1" && m.TriggerMode == MacroTriggerMode.Always).ToList();
            Assert.Equal(2, walk.Count);
            Assert.All(walk, m =>
            {
                var a = Assert.Single(m.Actions);
                Assert.Equal(MacroActionType.AxisScale, a.Type);
                // The engine multiplies by 1 + value / 32767, so 17918 / 32767.
                Assert.Equal(17918 - 32767, a.AxisValue);
            });
            Assert.Contains(walk, m => m.Actions[0].AxisTarget == MacroAxisTarget.LeftStickX);
            Assert.Contains(walk, m => m.Actions[0].AxisTarget == MacroAxisTarget.LeftStickY);
            Assert.DoesNotContain(p.Macros, m => m.LayerMask == "Base" && m.Actions.Any(a => a.Type == MacroActionType.AxisScale));
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.ControllerSetting);
        }

        [Fact]
        public void VirtualStickFields_PadForgeHasNoNameFor_AreNamed()
        {
            var r = Import(WithVirtualPad("""{"id":1,"type":"360","shifts":[{"shiftId":1,"sticks":[{"name":"right","initialSpeed":16,"acceleration":2}]}]}""",
                shifts: OneLayer));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.ControllerSetting
                && e.ReasonArgs.Contains("initialSpeed, acceleration"));
        }

        [Fact]
        public void StickSentToMotion_HasItsOwnReason()
        {
            var r = Import(File(mappings: """[{"condition":{"hardware":{"deviceId":1,"stick":"right","axis":"x"}},"hardware":{"stick":"gyro","axis":"x"}}]"""));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.MotionOutput);
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.ThirdStick);
        }

        [Fact]
        public void KeyboardActuation_IsReadWhereTheSchemaKeepsIt()
        {
            var r = Import(File(hardware: """[{"id":1,"name":"gamepad","convertToVirtual":true},{"id":11,"name":"keyboard","type":"standard","default":{"actuationButtons":[{"buttonId":30,"description":"DIK_A","actuation":{"actuationState":true,"actuationPoint":20}}]}},{"id":23,"name":"mouse"}]"""));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.AnalogActuation && e.SourcePath == "DIK_A");
        }

        // ── Safety ──────────────────────────────────────────────────────

        [Theory]
        [InlineData("""{"schemaVersion":"2.0"}""")]
        [InlineData("""{"hello":"world"}""")]
        [InlineData("""{"schemaVersion":4}""")]
        public void JsonThatIsNotSchema3_IsRefused(string json)
        {
            var r = Import(json);
            Assert.True(r.Refused);
            Assert.Equal(ImportReasons.RewasdOtherVersion, r.RefusedKey);
        }

        [Fact]
        public void MalformedJson_IsRefused()
            => Assert.Equal(ImportReasons.FileNotConfig, Import("""{"schemaVersion":"3.0" "config":{}}""").RefusedKey);

        [Fact]
        public void TrailingCommasAndComments_StillRead()
        {
            var r = Import("""
                { // hand edited
                  "schemaVersion":"3.0",
                  "devices":{"hardware":[{"id":1,"name":"gamepad","convertToVirtual":true},],"virtual":{"gamepads":[{"id":1,"type":"360"}]}},
                  "mappings":[],
                }
                """);
            Assert.False(r.Refused);
        }

        [Fact]
        public void Bom_IsTolerated()
        {
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(File())).ToArray();
            Assert.False(ForeignConfigImporter.Translate(bytes, "x.rewasd").Refused);
        }

        [Fact]
        public void OversizedFile_IsRefusedUnread()
        {
            string path = Path.Combine(Path.GetTempPath(), "pf-561-" + Guid.NewGuid().ToString("N") + ".rewasd");
            try
            {
                using (var f = System.IO.File.Create(path)) f.SetLength(17L * 1024 * 1024);
                var r = ForeignConfigImporter.Translate(path);
                Assert.Equal(ImportReasons.FileTooLarge, r.RefusedKey);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Fact]
        public void MissingFile_IsRefused()
            => Assert.Equal(ImportReasons.FileUnreadable, ForeignConfigImporter.Translate("C:/no/such/file.rewasd").RefusedKey);

        // ── The merged keyboard and mouse ───────────────────────────────

        [Fact]
        public void MergedGuids_MatchTheRowsThePcBuilds()
        {
            // RawInputListener's paths, hashed the way both wrappers hash them.
            Assert.Equal(new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("aggregate://keyboards"))).ToString(), AllKeyboards);
            Assert.Equal(new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("aggregate://mice"))).ToString(), AllMice);
        }

        [Fact]
        public void SlotsThatReadKeyboardOrMouse_ClaimTheMergedRows()
        {
            var r = Import(File(masks: "[" + Mask(1, 11, 30) + "," + Mask(2, 23, 1) + "]", mappings: "["
                + Map(1, """[{"gamepad":{"id":1,"buttonId":1}}]""") + ","
                + Map(2, "[" + Key(57) + "]") + "]"));
            var p = r.Profile;
            Assert.True(r.ReadsKeyboard);
            Assert.True(r.ReadsMouse);
            // No product guid: the apply's same-product fallback would rebind
            // these rows to whichever physical keyboard has a setting.
            Assert.All(p.Entries, e => Assert.Equal(Guid.Empty, e.ProductGuid));
            Assert.Contains(p.Entries, e => e.InstanceGuid.ToString() == AllKeyboards && e.MapTo == 0);
            Assert.Contains(p.Entries, e => e.InstanceGuid.ToString() == AllMice && e.MapTo == 1);
            Assert.DoesNotContain(p.Entries, e => e.InstanceGuid.ToString() == AllMice && e.MapTo == 0);
            var ps = Assert.Single(p.PadSettings);
            Assert.All(p.Entries, e => Assert.Equal(ps.PadSettingChecksum, e.PadSettingChecksum));
        }

        [Fact]
        public void APadOnlyProfile_ClaimsNoDevice_AndAsksForTheController()
        {
            var r = Import(File());
            Assert.Null(r.Profile.Entries);
            Assert.Null(r.Profile.PadSettings);
            Assert.True(r.ReadsController);
            Assert.False(r.ReadsKeyboard);
        }

        [Fact]
        public void KeyboardToGamepad_NeedsNoController()
        {
            var r = Import(File(hardware: """[{"id":11,"name":"keyboard"}]""",
                masks: "[" + Mask(1, 11, 30) + "]",
                mappings: "[" + Map(1, """[{"gamepad":{"id":1,"buttonId":1}}]""") + "]"));
            Assert.False(r.ReadsController);
            Assert.True(r.ReadsKeyboard);
            Assert.Single(Row(r.Profile, "ButtonA").Sources);
            Assert.Null(Row(r.Profile, "ButtonB"));
        }

        // ── A profile like any other ────────────────────────────────────

        [Fact]
        public void EveryTriggerSpec_ParsesBack()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 11, 30) + "," + Mask(3, 1, 40) + "," + Mask(4, 23, 64) + "]", mappings: "["
                + Map(1, "[" + Key(57) + "]", mode: "onetime") + ","
                + Map(2, "[" + Key(57) + "]", mode: "onetime") + ","
                + Map(3, "[" + Key(57) + "]", mode: "onetime") + ","
                + Map(4, "[" + Key(57) + "]", mode: "onetime") + "]"));
            foreach (var m in p.Macros)
                foreach (var spec in m.TriggerInputs.Split('|'))
                {
                    var entry = MacroItem.TriggerInputEntry.Parse(spec);
                    Assert.NotNull(entry);
                    Assert.Equal(spec, entry.Spec);
                }
        }

        [Fact]
        public void Profile_RoundTripsTheSettingsFile()
        {
            var p = Profile(File(shifts: OneLayer, masks: "[" + Mask(1, 11, 30) + "," + Mask(2, 1, 5) + "]", mappings: "["
                + Jump(2, 1) + ","
                + Map(1, """[{"gamepad":{"id":1,"buttonId":1}}]""") + ","
                + Map(1, "[" + Key(57) + """,{"pause":{"value":40}},""" + Key(28) + "]", mode: "onetime", shift: 1) + "]",
                config: """{"appName":"x","led":{"default":{"mode":"solid","colors":[[1,2,3]]}}}"""));

            var serializer = new XmlSerializer(typeof(ProfileData));
            using var buffer = new MemoryStream();
            serializer.Serialize(buffer, p);
            buffer.Position = 0;
            var back = (ProfileData)serializer.Deserialize(buffer);

            Assert.Equal(p.Name, back.Name);
            Assert.Equal(p.Macros.Length, back.Macros.Length);
            Assert.Equal(p.SlotMappingSets[0].Rows.Count, back.SlotMappingSets[0].Rows.Count);
            Assert.Equal(p.SlotMappingSets[0].ShiftActivators.Count, back.SlotMappingSets[0].ShiftActivators.Count);
            Assert.Equal(p.Entries.Length, back.Entries.Length);
            Assert.Equal(p.SlotControllerTypes[0], back.SlotControllerTypes[0]);
            Assert.True(back.SlotMappingSets[0].Authoritative);
        }

        [Fact]
        public void ImportIsPure_TwoReadsAgree()
        {
            string json = File(shifts: OneLayer, masks: "[" + Mask(1, 1, 1) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]");
            var a = Profile(json);
            var b = Profile(json);
            Assert.NotEqual(a.Id, b.Id);
            Assert.Equal(a.SlotMappingSets[0].Rows.Count, b.SlotMappingSets[0].Rows.Count);
        }

        // ── Audit 2026-11-08 ────────────────────────────────────────────

        private static string PadOut(int button) => "[{\"gamepad\":{\"id\":1,\"buttonId\":" + button + "}}]";

        private static bool Names(ForeignImportResult r, string reason, string arg)
            => r.Report.Entries.Any(e => e.ReasonKey == reason && e.ReasonArgs != null && e.ReasonArgs.Contains(arg));

        [Fact]
        public void MouseScrollUp_ReadsTheUpperHalfOfTheWheel()
        {
            var p = Profile(File(masks: "[" + Mask(1, 23, 171) + "," + Mask(2, 23, 172) + "]",
                mappings: "[" + Map(1, PadOut(1)) + "," + Map(2, PadOut(2)) + "]"));
            var up = Row(p, "ButtonA").Sources.Single(s => s.Descriptor == "Axis 2");
            var down = Row(p, "ButtonB").Sources.Single(s => s.Descriptor == "Axis 2");
            // Windows reports a notch up as +120, which the mouse puts above
            // center (SdlMouseWrapper's Axis 2).
            var wheelUp = Centered();
            wheelUp.Axis[2] = 65535;
            var wheelDown = Centered();
            wheelDown.Axis[2] = 0;
            Assert.True(ButtonRead(wheelUp, up));
            Assert.False(ButtonRead(wheelDown, up));
            Assert.True(ButtonRead(wheelDown, down));
            Assert.False(ButtonRead(wheelUp, down));
        }

        [Fact]
        public void ACurveEndingAtAThirdOfTravel_ReachesFullOutputThere()
        {
            // Vergil.rewasd's left stick: travel and value on one 0-32767
            // scale, full output at 31% of the travel and held from there.
            var r = Import(File(hardware: Tuned(
                """{"sticks":[{"name":"left","curves":{"points":{"horizontal":[{"id":1,"value":2293,"travel":2531},{"id":2,"value":5570,"travel":4781},{"id":3,"value":9830,"travel":7594},{"id":4,"value":32768,"travel":10125}]}}}]}""")));
            string card = r.Profile.SlotMappingSets[0].WorkshopCurves.Split('|')
                .Single(c => c.StartsWith("LeftThumbAxisX=", StringComparison.Ordinal))["LeftThumbAxisX=".Length..];
            Assert.Equal("0,0;0.0772,0.07;0.1459,0.17;0.2318,0.3;0.309,1;1,1", card);
        }

        [Fact]
        public void ANumpadEnterOutput_SendsNumpadEnter()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 1, 2) + "]",
                mappings: "[" + Map(1, "[" + Key(156) + "]") + "," + Map(2, "[" + Key(28) + "]") + "]"));
            Assert.NotNull(Row(p, "KbmKey88"));
            Assert.NotNull(Row(p, "KbmKey0D"));
        }

        [Fact]
        public void AKeyReadsThroughTheActiveLayout()
        {
            // QWERTZ: the bottom-left letter key (scan 0x2C) types Y.
            static int Qwertz(int scan) => scan switch { 0x15 => 0x5A, 0x2C => 0x59, _ => 0 };
            Assert.True(DirectInputKeys.TryGetVirtualKey(0x2C, Qwertz, out byte vk));
            Assert.Equal(0x59, vk);
            Assert.True(DirectInputKeys.TryGetVirtualKey(0x15, Qwertz, out vk));
            Assert.Equal(0x5A, vk);
            // A key only a regional layout has: ABNT C1 on a Brazilian one.
            Assert.True(DirectInputKeys.TryGetVirtualKey(0x73, s => s == 0x73 ? 0xC1 : 0, out vk));
            Assert.Equal(0xC1, vk);
            // The numpad, the modifiers and the E0 keys keep their own codes,
            // whatever the layout says.
            static int Home(int scan) => 0x24;
            Assert.True(DirectInputKeys.TryGetVirtualKey(0x47, Home, out vk));
            Assert.Equal(0x67, vk);
            Assert.True(DirectInputKeys.TryGetVirtualKey(0x2A, Home, out vk));
            Assert.Equal(0xA0, vk);
            Assert.True(DirectInputKeys.TryGetVirtualKey(0xC8, Home, out vk));
            Assert.Equal(0x26, vk);
            // A layout with no key there falls back to the table.
            Assert.True(DirectInputKeys.TryGetVirtualKey(0x2C, _ => 0, out vk));
            Assert.Equal(0x5A, vk);
        }

        [Fact]
        public void ACompensatedStickOutput_SaysSo()
        {
            var r = Import(File(masks: "[" + Mask(1, 23, 67) + "]",
                mappings: "[" + Map(1, """[{"gamepad":{"id":1,"buttonId":50,"absolute":false,"compensation":true,"value":32767}}]""") + "]"));
            Assert.True(Names(r, ImportReasons.ControllerSetting, "compensation"));
        }

        [Fact]
        public void ARumbleStepsMacroFlag_IsNamed()
        {
            var r = Import(File(masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, """[{"rumble":{"duration":100,"amplitude":{"motor":{"left":50}},"macro":true}}]""", mode: "onetime") + "]"));
            Assert.True(Names(r, ImportReasons.ControllerSetting, "macro"));
        }

        [Fact]
        public void AVirtualPadsPerformanceAndHub_AreNamed()
        {
            var r = Import(WithVirtualPad("""{"id":1,"type":"360","performance":"high","virtualUsbHub":true}"""));
            Assert.True(Names(r, ImportReasons.ControllerSetting, "performance"));
            Assert.True(Names(r, ImportReasons.ControllerSetting, "virtualUsbHub"));
            var plain = Import(WithVirtualPad("""{"id":1,"type":"360","performance":"normal"}"""));
            Assert.False(Names(plain, ImportReasons.ControllerSetting, "performance"));
        }

        private static string Set(int count)
            => "[" + string.Join(",", Enumerable.Range(1, count).Select(b => "{\"deviceId\":1,\"buttonId\":" + b + "}")) + "]";

        [Fact]
        public void AMaskOfFiveInputs_IsRefused()
        {
            // The schema's set holds one to four inputs.
            Assert.Equal(ImportReasons.FileTooLarge, Import(File(masks: "[{\"id\":1,\"set\":" + Set(5) + "}]")).RefusedKey);
            Assert.Equal(ImportReasons.FileTooLarge, Import(File(shifts: "[{\"id\":1,\"type\":\"default\",\"set\":" + Set(5) + "}]")).RefusedKey);
            Assert.False(Import(File(masks: "[{\"id\":1,\"set\":" + Set(4) + "}]")).Refused);
        }

        [Fact]
        public void CrosshairsNineAndTen_DoNotShareOverlayEight()
        {
            static string Cmd(int id) => "{\"command\":{\"id\":" + id + "}}";
            var r = Import(File(commands: """[{"id":1,"type":108,"name":"c8"},{"id":2,"type":109,"name":"c9"},{"id":3,"type":110,"name":"c10"}]""",
                masks: "[" + Mask(1, 1, 1) + "," + Mask(2, 1, 2) + "," + Mask(3, 1, 3) + "]",
                mappings: "[" + Map(1, "[" + Cmd(1) + "]", mode: "onetime") + "," + Map(2, "[" + Cmd(2) + "]", mode: "onetime")
                    + "," + Map(3, "[" + Cmd(3) + "]", mode: "onetime") + "]"));
            var overlays = r.Profile.Macros.SelectMany(m => m.Actions).Where(a => a.Type == MacroActionType.ToggleScreenOverlay).ToList();
            Assert.Equal(8, Assert.Single(overlays).ScreenOverlayNumber);
            Assert.Equal(2, r.Report.Entries.Count(e => e.ReasonKey == ImportReasons.NinthCrosshair));
        }

        [Fact]
        public void AMaskInputWithExtended_SaysSo()
        {
            var r = Import(File(masks: """[{"id":1,"set":[{"deviceId":1,"buttonId":1,"extended":2}]}]""",
                mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.True(Names(r, ImportReasons.ControllerSetting, "extended"));
        }

        private static int Down(int percent) => 32768 + 32767 * percent / 100;
        private static int Left(int percent) => 32768 - 32767 * percent / 100;

        [Fact]
        public void AStickZoneDirection_ReadsInsideItsZone()
        {
            // 113 LeftStickMedUpZone: up, between the medium and high edges
            // (66% and 85% by default).
            var p = Profile(File(masks: "[" + Mask(1, 1, 113) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var src = Row(p, "KbmKey20").Sources[0];
            var state = Centered();
            state.Axis[1] = Up(75);
            Assert.True(ButtonRead(state, src));
            state.Axis[1] = Up(50);
            Assert.False(ButtonRead(state, src));
            state.Axis[1] = Up(95);
            Assert.False(ButtonRead(state, src));
            state.Axis[1] = Down(75);
            Assert.False(ButtonRead(state, src));
        }

        [Fact]
        public void TheUpLeftDiagonal_ReadsASector()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 175) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var src = Row(p, "KbmKey20").Sources[0];
            var state = Centered();
            state.Axis[0] = Left(60);
            state.Axis[1] = Up(60);
            Assert.True(ButtonRead(state, src));
            state.Axis[0] = 32768;
            state.Axis[1] = Up(80);
            Assert.False(ButtonRead(state, src));
            state.Axis[0] = Left(80);
            state.Axis[1] = 32768;
            Assert.False(ButtonRead(state, src));
        }

        [Fact]
        public void GyroLean_ReadsTheYawHalves()
        {
            // reWASD's help: "By default, Lean is a yaw rotation." Gyro Yaw's
            // upper half turns the nose left.
            var p = Profile(File(masks: "[" + Mask(1, 1, 72) + "," + Mask(2, 1, 73) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]") + "," + Map(2, "[" + Key(28) + "]") + "]"));
            var left = Row(p, "KbmKey20").Sources[0];
            var right = Row(p, "KbmKey0D").Sources[0];
            Assert.Equal("Gyro Yaw", left.Descriptor);
            Assert.True(left.HalfAxis);
            Assert.False(left.Invert);
            Assert.Equal("Gyro Yaw", right.Descriptor);
            Assert.True(right.Invert);
        }

        [Fact]
        public void AStickToGyroMapping_DrivesTheMotionRows()
        {
            var r = Import(File(virtualType: "ds4",
                mappings: """[{"condition":{"hardware":{"deviceId":1,"stick":"right","axis":"x"}},"hardware":{"stick":"gyro","axis":"x"}},{"condition":{"hardware":{"deviceId":1,"stick":"right","axis":"y"}},"hardware":{"stick":"gyro","axis":"y"}}]"""));
            Assert.Contains(Row(r.Profile, "MotionYaw").Sources, s => s.Descriptor == "Gamepad RightStickX");
            Assert.Contains(Row(r.Profile, "MotionPitch").Sources, s => s.Descriptor == "Gamepad RightStickY");
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.MotionOutput);
        }

        [Fact]
        public void ALayerOnlyUnmap_BlocksOnThatLayer()
        {
            var r = Import(File(shifts: OneLayer, masks: "[" + Mask(3, 1, 5) + "]",
                mappings: "[" + Jump(3, 1) + "," + """{"condition":{"shiftId":1,"hardware":{"deviceId":11,"buttonId":17,"description":"DIK_W"}},"hardware":{"unmap":true}}]"""));
            var block = Assert.Single(r.Profile.Macros, m => m.ConsumeTriggerButtons);
            Assert.Equal("Shift1", block.LayerMask);
        }

        /// <summary>A shift inherits Main's Unmap for every control it does
        /// not map itself (reWASD help, Shift mode, and forum thread 217280).
        /// A layer that replaces Base takes no Base macro, so it takes its
        /// own copy of Base's block, except for a control it maps: DIK_C,
        /// which Shift1 maps, takes none there.</summary>
        [Fact]
        public void ALayerThatReplacesBase_TakesBasesBlock_ForAControlItDoesNotMap()
        {
            var p = Profile(File(shifts: OneLayer,
                masks: "[" + Mask(1, 1, 1) + "," + Mask(3, 1, 5) + "," + Mask(4, 11, 46) + "]",
                mappings: "[" + Jump(3, 1) + ","
                    + Map(1, "[" + Key(57) + "]", mode: "onetime") + ","
                    + Map(1, "[" + Key(15) + "]", mode: "onetime", shift: 1) + ","
                    + Map(4, PadOut(3), shift: 1) + ","
                    + """{"condition":{"hardware":{"deviceId":11,"buttonId":48,"description":"DIK_B"}},"hardware":{"unmap":true}},"""
                    + """{"condition":{"hardware":{"deviceId":11,"buttonId":46,"description":"DIK_C"}},"hardware":{"unmap":true}}]"""));
            Assert.False(p.SlotMappingSets[0].ShiftActivators.Single().InheritUnmapped);
            var blocks = p.Macros.Where(m => m.ConsumeTriggerButtons).ToList();
            var baseBlocks = blocks.Where(m => m.LayerMask == "Base").ToList();
            Assert.Equal(2, baseBlocks.Count);
            var copy = Assert.Single(blocks, m => m.LayerMask == "Shift1");
            var original = Assert.Single(baseBlocks, m => m.TriggerInputs == copy.TriggerInputs);
            Assert.Equal("Block Keyboard B", original.Name);
        }

        /// <summary>"Do not inherit" makes a key "work normally, as if no
        /// mappings are applied to it" on the shift (reWASD help, Shift
        /// mode), so Base's unmap of DIK_C must not reach the layer. Base's
        /// block runs on every layer that inherits, so that layer replaces
        /// Base, and it takes no copy of the block.</summary>
        [Fact]
        public void ALayerThatDoesNotInheritABaseUnmap_ReplacesBase_AndTakesNoBlock()
        {
            var p = Profile(File(shifts: """[{"id":1,"type":"default","unheritableMasks":[4]}]""",
                masks: "[" + Mask(3, 1, 5) + "," + Mask(4, 11, 46) + "]",
                mappings: "[" + Jump(3, 1) + ","
                    + """{"condition":{"hardware":{"deviceId":11,"buttonId":46,"description":"DIK_C"}},"hardware":{"unmap":true}}]"""));
            Assert.False(p.SlotMappingSets[0].ShiftActivators.Single().InheritUnmapped);
            var block = Assert.Single(p.Macros, m => m.ConsumeTriggerButtons);
            Assert.Equal("Base", block.LayerMask);
        }

        /// <summary>The hook eats a row's key only on the layers where the
        /// row runs. A key one layer unmaps and only another layer's row
        /// reads reaches Windows on the first layer, so that layer keeps its
        /// block. Before, any row on any layer reading the key dropped it.</summary>
        [Fact]
        public void ALayerUnmap_KeepsItsBlock_WhenOnlyAnotherLayersRowReadsTheKey()
        {
            var r = Import(File(
                shifts: """[{"id":1,"type":"default","description":"One"},{"id":2,"type":"default","description":"Two"}]""",
                masks: "[" + Mask(3, 1, 5) + "," + Mask(4, 1, 6) + "," + Mask(1, 11, 30) + "]",
                mappings: "[" + Jump(3, 1) + "," + Jump(4, 2) + "," + Map(1, PadOut(1), shift: 2) + ","
                    + """{"condition":{"shiftId":1,"hardware":{"deviceId":11,"buttonId":30,"description":"DIK_A"}},"hardware":{"unmap":true}}]"""));
            Assert.Contains(r.Profile.SlotMappingSets[0].Rows, row => row.LayerMask == "Shift2"
                && row.Sources.Any(s => s.Descriptor == "Button 65"));
            var block = Assert.Single(r.Profile.Macros, m => m.ConsumeTriggerButtons);
            Assert.Equal("Shift1", block.LayerMask);
        }

        [Fact]
        public void ALayerButtonOnAStickDirection_UsesTheFilesZone()
        {
            var r = Import(File(shifts: """[{"id":1,"type":"default","set":[{"deviceId":1,"buttonId":40}]}]""",
                hardware: Tuned("""{"sticks":[{"name":"left","zones":[{"name":"low","x":16384}]}]}""")));
            var act = r.Profile.SlotMappingSets[0].ShiftActivators.Single(a => a.LayerMask == "Shift1");
            Assert.InRange(act.AxisThreshold, 0.49, 0.51);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(13573)]
        public void AnUntouchedAxisRange_SaysNothing(int value)
        {
            // reWASD 9.1 and 9.3 wrote 1 on sticks nobody touched (Miner
            // Warfare 9.1.2, TATE - Rotate -90 9.3.0), and 13573 is the default
            // (reWASDCommon Constants.DefaultStickSensitivity).
            var r = Import(File(hardware: Tuned("{\"sticks\":[{\"name\":\"left\",\"sensitivity\":{\"x\":" + value + ",\"y\":" + value + "}}]}")));
            Assert.Equal(1.0, Row(r.Profile, "LeftThumbAxisX").Sources[0].Sensitivity);
            Assert.Equal(1.0, Row(r.Profile, "LeftThumbAxisY").Sources[0].Sensitivity);
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.ControllerSetting && e.ReasonArgs.Contains("sensitivity"));
        }

        [Fact]
        public void ATurboOnTheTouchpadClick_SaysAMacroCannotPressIt()
        {
            var r = Import(File(virtualType: "ds4", masks: "[" + Mask(1, 1, 1) + "]",
                mappings: "[" + Map(1, PadOut(99), mode: "turbo") + "]"));
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Skipped && e.ReasonKey == ImportReasons.MacroCannotPress);
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.NoSuchPadButton);
        }

        // ── Audit 2026-11-08, second pass ───────────────────────────────

        private const float G = 9.81f;

        /// <summary>A source read through PadForge's own gyro and lean
        /// reads, with the device's motion as given: rates in rad/s on SDL's
        /// axes, and the accelerometer's reading, which points up. The lean
        /// latches its neutral on the first read, so <paramref name="rest"/>
        /// is read first.</summary>
        private static T ThroughMotion<T>(Func<CustomInputState, T> read, (float x, float y, float z) rest,
            (float x, float y, float z) now, float pitch = 0, float yaw = 0, float roll = 0)
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldGravity = SourceCoercion.GravityProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.AimEngageStateProvider = null;
                SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning
                {
                    SensH = 1, SensV = 1, OutputCurve = "Linear", Space = "Local", Grip = "Pointing",
                };
                var gravity = rest;
                SourceCoercion.GravityProvider = _ => gravity;
                read(Centered());
                gravity = now;
                var state = Centered();
                state.Gyro[0] = pitch;
                state.Gyro[1] = yaw;
                state.Gyro[2] = roll;
                return read(state);
            }
            finally
            {
                SourceCoercion.ResetGyroLeanNeutral();
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GravityProvider = oldGravity;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.AimEngageStateProvider = oldEngage;
            }
        }

        private static readonly (float, float, float) Flat = (0f, G, 0f);

        private static bool Pressed(MappingSource src, float pitch = 0, float yaw = 0, float roll = 0)
            => ThroughMotion(s => SourceCoercion.EvaluateForButtonTarget(s, src, 50, 0, ""), Flat, Flat, pitch, yaw, roll);

        private static bool PressedAtTilt(MappingSource src, (float, float, float) tilted)
            => ThroughMotion(s => SourceCoercion.EvaluateForButtonTarget(s, src, 50, 0, ""), Flat, tilted);

        private static MappingSource KeySource(ProfileData p, string target)
            => Row(p, target).Sources.Single(s => s.Descriptor.StartsWith("Gyro ", StringComparison.Ordinal));

        private static string GyroMasks(params int[] ids)
            => "[" + string.Join(",", ids.Select((id, i) => Mask(i + 1, 1, id))) + "]";

        private static string KeysFor(params int[] scans)
            => "[" + string.Join(",", scans.Select((sc, i) => Map(i + 1, "[" + Key(sc) + "]"))) + "]";

        [Fact]
        public void InGyroMode_LeftAndRightReadRoll_AndLeanReadsYaw()
        {
            // reWASD's help: "By default in reWASD, in Gyro mode, yaw stands
            // for Lean mappings, while the roll is used to look around."
            var r = Import(File(masks: GyroMasks(70, 71, 72, 73, 68), mappings: KeysFor(30, 31, 32, 33, 34)));
            var left = KeySource(r.Profile, "KbmKey41");
            var right = KeySource(r.Profile, "KbmKey53");
            var leanLeft = KeySource(r.Profile, "KbmKey44");
            var leanRight = KeySource(r.Profile, "KbmKey46");
            var up = KeySource(r.Profile, "KbmKey47");

            // Rolling right lowers the right side: SDL's roll is negative.
            Assert.True(Pressed(right, roll: -2f));
            Assert.False(Pressed(left, roll: -2f));
            Assert.True(Pressed(left, roll: 2f));
            // Turning the nose left is a positive SDL yaw.
            Assert.True(Pressed(leanLeft, yaw: 2f));
            Assert.False(Pressed(leanRight, yaw: 2f));
            Assert.True(Pressed(leanRight, yaw: -2f));
            // The nose up is a positive SDL pitch.
            Assert.True(Pressed(up, pitch: 2f));
            // One line says which axes these are, whatever reads them.
            Assert.Single(r.Report.Entries, e => e.ReasonKey == ImportReasons.GyroAxes);
        }

        [Fact]
        public void AGyroStickSource_PushesRightOnARollToTheRight()
        {
            var r = Import(File(mappings: """[{"condition":{"hardware":{"deviceId":1,"stick":"gyro","axis":"x"}},"hardware":{"stick":"right","axis":"x"}}]"""));
            var src = Row(r.Profile, "RightThumbAxisX").Sources.Single(s => s.Descriptor.StartsWith("Gyro ", StringComparison.Ordinal));
            // reWASD's help (Gyroscope): "roll your gamepad to the right to
            // look right". Rolling right lowers the right side, a negative SDL
            // roll. The row is read the way the engine reads it, with the
            // axis-frame correction a stick X target applies to the gyro's
            // horizontal family. Gyro to a stick reaches full scale at 500
            // deg/s, so 2 rad/s is about a quarter.
            short pushed = ThroughMotion(s => GyroStickSignProbe.Written(s, src, "RightThumbAxisX"), Flat, Flat, roll: -2f);
            Assert.True(pushed > 3000, $"a roll to the right should push right, wrote {pushed}");
            short back = ThroughMotion(s => GyroStickSignProbe.Written(s, src, "RightThumbAxisX"), Flat, Flat, roll: 2f);
            Assert.True(back < -3000, $"a roll to the left should push left, wrote {back}");
            Assert.Single(r.Report.Entries, e => e.ReasonKey == ImportReasons.GyroAxes);
        }

        [Fact]
        public void AGyroStickSource_TheFilesInvertedFlagPushesLeftOnARollToTheRight()
        {
            var r = Import(File(mappings: GyroTo("x", "right", inverted: true)));
            var src = GyroSourceOn(r.Profile, "RightThumbAxisX");
            short written = ThroughMotion(s => GyroStickSignProbe.Written(s, src, "RightThumbAxisX"), Flat, Flat, roll: -2f);
            Assert.True(written < -3000, $"inverted: a roll to the right should push left, wrote {written}");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AGyroMouseSource_MovesRightOnARollToTheRight(bool inverted)
        {
            // reWASD's help: "roll your gamepad to the right to look right".
            // The roll lane is SDL's roll negated, and the mouse lane negates
            // it into screen space, so without an Invert of its own the row
            // moves the cursor left. The row is read through the mouse lane
            // and the KBM controller's screen-space negation.
            var r = Import(File(mappings: GyroTo("x", "mouse", inverted)));
            var src = GyroSourceOn(r.Profile, "KbmMouseX");
            Assert.Equal("Gyro Roll", src.Descriptor);
            float screen = GyroStickSignProbe.AtRate(s => GyroStickSignProbe.ScreenCounts(s, src, forX: true), roll: -2f);
            if (inverted) Assert.True(screen < 0f, $"inverted: a roll to the right should move the cursor left, moved {screen}");
            else Assert.True(screen > 0f, $"a roll to the right should move the cursor right, moved {screen}");
        }

        /// <summary>A mapping from the gyro's own stick axis to a stick or
        /// the mouse, with the output's "inverted" flag.</summary>
        private static string GyroTo(string axis, string toStick, bool inverted = false)
            => "[{\"condition\":{\"hardware\":{\"deviceId\":1,\"stick\":\"gyro\",\"axis\":\"" + axis + "\"}},"
             + "\"hardware\":{\"stick\":\"" + toStick + "\",\"axis\":\"" + axis + "\""
             + (inverted ? ",\"inverted\":true" : "") + "}}]";

        private static MappingSource GyroSourceOn(ProfileData p, string target)
            => Row(p, target).Sources.Single(s => s.Descriptor.StartsWith("Gyro ", StringComparison.Ordinal));

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AGyroStickSource_PushesUpOnTheNoseUp(bool inverted)
        {
            // reWASD's help (Gyroscope, Advanced settings for Gyro mode): "By
            // default, you need to move the gamepad's 'nose' up to look up".
            // The nose up is a positive SDL pitch. The row is read the way the
            // engine reads it, axis-frame correction and Y negate included,
            // so the written stick field is up positive.
            var r = Import(File(mappings: GyroTo("y", "right", inverted)));
            var src = GyroSourceOn(r.Profile, "RightThumbAxisY");
            Assert.Equal("Gyro Pitch", src.Descriptor);
            short written = ThroughMotion(s => GyroStickSignProbe.Written(s, src, "RightThumbAxisY"), Flat, Flat, pitch: 2f);
            if (inverted) Assert.True(written < -3000, $"inverted: nose up should push down, wrote {written}");
            else Assert.True(written > 3000, $"nose up should push the stick up, wrote {written}");
        }

        [Fact]
        public void AGyroMouseSource_MovesUpOnTheNoseUp()
        {
            // The mouse lane already turns the nose up into the cursor up, so
            // the pitch row to the mouse carries no Invert of its own.
            var r = Import(File(mappings: GyroTo("y", "mouse")));
            var src = GyroSourceOn(r.Profile, "KbmMouseY");
            Assert.False(src.Invert);
            float screen = GyroStickSignProbe.AtRate(s => GyroStickSignProbe.ScreenCounts(s, src, forX: false), pitch: 2f);
            Assert.True(screen < 0f, $"nose up should move the cursor up, moved {screen}");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void InTiltMode_AGyroStickSourcePushesUpOnTheNoseUp(bool inverted)
        {
            // reWASD's help (Gyroscope, Advanced settings for Tilt mode)
            // repeats it: "By default, you need to move the gamepad's 'nose'
            // up to look up". Gyro Lean Y is positive with the nose up, which
            // the Y negate writes as the stick down without an Invert.
            var r = Import(File(hardware: TiltPad, mappings: GyroTo("y", "right", inverted)));
            var src = GyroSourceOn(r.Profile, "RightThumbAxisY");
            Assert.Equal("Gyro Lean Y", src.Descriptor);
            float s70 = MathF.Sin(70 * MathF.PI / 180f), c70 = MathF.Cos(70 * MathF.PI / 180f);
            var noseUp = (0f, G * c70, -G * s70);
            short written = ThroughMotion(s => GyroStickSignProbe.Written(s, src, "RightThumbAxisY"), Flat, noseUp);
            if (inverted) Assert.True(written < -16000, $"inverted: nose up should push down, wrote {written}");
            else Assert.True(written > 16000, $"nose up should push the stick up, wrote {written}");
        }

        private static string TiltPad => Tuned("""{"sticks":[{"name":"gyro","tilt":true}]}""");

        [Fact]
        public void InTiltMode_DirectionsReadTheTiltAngle()
        {
            var r = Import(File(hardware: TiltPad, masks: GyroMasks(70, 71, 68, 69), mappings: KeysFor(30, 31, 32, 33)));
            var left = KeySource(r.Profile, "KbmKey41");
            var right = KeySource(r.Profile, "KbmKey53");
            var up = KeySource(r.Profile, "KbmKey44");
            var down = KeySource(r.Profile, "KbmKey46");
            float s70 = MathF.Sin(70 * MathF.PI / 180f), c70 = MathF.Cos(70 * MathF.PI / 180f);
            // The right edge down: the reading, which points up, leans to -X.
            var rightDown = (-G * s70, G * c70, 0f);
            var noseUp = (0f, G * c70, -G * s70);
            Assert.True(PressedAtTilt(right, rightDown));
            Assert.False(PressedAtTilt(left, rightDown));
            Assert.True(PressedAtTilt(up, noseUp));
            Assert.False(PressedAtTilt(down, noseUp));
            // The tilt switch is read, so it has no line of its own, and Tilt
            // mode's directions are no roll or yaw.
            Assert.False(Names(r, ImportReasons.ControllerSetting, "gyro"));
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.GyroAxes);
        }

        [Fact]
        public void InTiltMode_AGyroStickSourceReadsTheTiltAngle_AndLeanIsSkipped()
        {
            var r = Import(File(hardware: TiltPad, masks: GyroMasks(72),
                mappings: "[" + Map(1, "[" + Key(30) + "]") + ","
                    + """{"condition":{"hardware":{"deviceId":1,"stick":"gyro","axis":"x"}},"hardware":{"stick":"right","axis":"x"}}]"""));
            var src = Row(r.Profile, "RightThumbAxisX").Sources.Single(s => s.Descriptor.StartsWith("Gyro ", StringComparison.Ordinal));
            Assert.Equal("Gyro Lean X", src.Descriptor);
            float s70 = MathF.Sin(70 * MathF.PI / 180f), c70 = MathF.Cos(70 * MathF.PI / 180f);
            // Read the way the engine reads the row. The axis-frame correction
            // leaves the lean pair alone, so the right edge down pushes right.
            short pushed = ThroughMotion(s => GyroStickSignProbe.Written(s, src, "RightThumbAxisX"), Flat, (-G * s70, G * c70, 0f));
            Assert.True(pushed > 16000, $"the right edge down should push right, wrote {pushed}");
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Skipped && e.ReasonKey == ImportReasons.TiltLean);
        }

        [Theory]
        [InlineData(176, 1, 1)]
        [InlineData(177, -1, -1)]
        [InlineData(178, 1, -1)]
        public void TheOtherLeftStickDiagonals_ReadTheirSectors(int id, int right, int up)
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, id) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var src = Row(p, "KbmKey20").Sources[0];
            var state = Centered();
            state.Axis[0] = right > 0 ? Right(60) : Left(60);
            state.Axis[1] = up > 0 ? Up(60) : Down(60);
            Assert.True(ButtonRead(state, src));
            state.Axis[0] = 32768;
            state.Axis[1] = up > 0 ? Up(80) : Down(80);
            Assert.False(ButtonRead(state, src));
        }

        [Fact]
        public void TheRightStickDiagonals_ReadTheRightStick()
        {
            var p = Profile(File(masks: "[" + Mask(1, 1, 181) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            var src = Row(p, "KbmKey20").Sources[0];
            var state = Centered();
            state.Axis[3] = Left(60);
            state.Axis[4] = Down(60);
            Assert.True(ButtonRead(state, src));
            state.Axis[0] = Left(60);
            state.Axis[1] = Down(60);
            state.Axis[3] = 32768;
            state.Axis[4] = 32768;
            Assert.False(ButtonRead(state, src));
        }

        [Fact]
        public void AThirdStickDiagonal_SaysPadForgeReadsTwoSticks()
        {
            var r = Import(File(masks: "[" + Mask(1, 1, 220) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Skipped && e.ReasonKey == ImportReasons.ThirdStick);
        }

        [Fact]
        public void ATouchpadDiagonal_ReadsTwoDirectionsTogether_AndSaysWhatTheTouchpadNeeds()
        {
            var r = Import(File(virtualType: "ds4", masks: "[" + Mask(1, 1, 95) + "," + Mask(2, 1, 107) + "]",
                mappings: "[" + Map(1, "[" + Key(57) + "]") + "," + Map(2, "[" + Key(28) + "]") + "]"));
            var upLeft = Row(r.Profile, "KbmKey20").Sources[0];
            Assert.Equal("Touchpad 0 DPadUp", upLeft.Descriptor);
            Assert.Equal("Touchpad 0 DPadLeft", upLeft.GateDescriptor);
            var downRight = Row(r.Profile, "KbmKey0D").Sources[0];
            Assert.Equal("Touchpad 1 DPadDown", downRight.Descriptor);
            Assert.Equal("Touchpad 1 DPadRight", downRight.GateDescriptor);
            Assert.Single(r.Report.Entries, e => e.ReasonKey == ImportReasons.TouchpadDiagonal);
        }

        [Theory]
        [InlineData(74)]
        [InlineData(76)]
        [InlineData(145)]
        [InlineData(162)]
        public void AGyroZone_SaysWhyItIsSkipped(int id)
        {
            var r = Import(File(masks: "[" + Mask(1, 1, id) + "]", mappings: "[" + Map(1, "[" + Key(57) + "]") + "]"));
            Assert.Contains(r.Report.Entries, e => e.Status == TranslationStatus.Skipped && e.ReasonKey == ImportReasons.GyroZone);
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.UnknownInput);
        }

        [Fact]
        public void TheTestHostReadsKeysThroughTheUsTable()
        {
            // The fence keeps the build machine's layout out of every import
            // test, and the layout path is driven by AKeyReadsThroughTheActiveLayout.
            Assert.True(DirectInputKeys.OffLimitsForTest);
            Assert.True(DirectInputKeys.TryGetVirtualKey(0x2C, out byte vk));
            Assert.Equal(0x5A, vk);
        }

        [Theory]
        [InlineData("PadForge.App/MainWindow.xaml.cs")]
        [InlineData("PadForge.App/Views/IconPicker.xaml.cs")]
        [InlineData("PadForge.App/Views/LearnHandheldButtonDialog.xaml.cs")]
        [InlineData("PadForge.App/Views/ManageProfilesDialog.xaml.cs")]
        [InlineData("PadForge.App/Views/MapKeyLightsDialog.xaml.cs")]
        [InlineData("PadForge.App/ViewModels/PadViewModel.AudioDsp.cs")]
        [InlineData("PadForge.App/Views/PadPage.xaml.cs")]
        public void FileDialogTypeNames_ComeFromTheStrings(string file)
        {
            // A file dialog's type names are words the user reads, so every
            // one comes from the strings. A literal in a Filter assignment may
            // hold extensions and nothing else.
            string src = System.IO.File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(file.Replace('/', Path.DirectorySeparatorChar)));
            var filters = System.Text.RegularExpressions.Regex.Matches(src, @"Filter\s*=\s*[^\n]*(\n\s*\+[^\n]*)*");
            Assert.NotEmpty(filters);
            foreach (System.Text.RegularExpressions.Match m in filters)
            {
                string code = System.Text.RegularExpressions.Regex.Replace(m.Value, @"\{[^}]*\}", "");
                foreach (System.Text.RegularExpressions.Match literal in System.Text.RegularExpressions.Regex.Matches(code, "\"([^\"]*)\""))
                {
                    string words = System.Text.RegularExpressions.Regex.Replace(literal.Groups[1].Value, @"\*\.[A-Za-z0-9]+", "");
                    Assert.DoesNotMatch("[A-Za-z]{3,}", words);
                }
            }
        }

        [Fact]
        public void ThePreviewNamesTheProfileTheAddCreates()
        {
            var saved = SettingsManager.Profiles.ToList();
            try
            {
                SettingsManager.Profiles.Clear();
                Assert.Equal("Test Game", PadForge.Views.ImportConfigDialog.PreviewName(Import(File())));
                SettingsManager.Profiles.Add(new ProfileData { Name = "test game" });
                Assert.Equal("Test Game (2)", PadForge.Views.ImportConfigDialog.PreviewName(Import(File())));
                Assert.Equal("Test Game (2)", SettingsManager.UniqueProfileName("Test Game"));
            }
            finally
            {
                SettingsManager.Profiles.Clear();
                SettingsManager.Profiles.AddRange(saved);
            }
        }
    }
}
