using System;
using System.Collections.Generic;
using PadForge.Resources.Strings;
using PadForge.Engine.Common;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// What the user reads of an assistant's tools (#597): each tool's line in
    /// Activity and the words of the questions the request card puts. These
    /// are interface strings in the language in use. The words an assistant
    /// reads, the tools' names, titles and descriptions, are English and live
    /// with the tools.
    /// </summary>
    internal static class AssistantToolWords
    {
        /// <summary>The longest stretch of a program's text a line
        /// carries.</summary>
        private const int LongestDetail = 60;

        private static readonly Dictionary<string, Func<Strings, string>> Phrases = new(StringComparer.Ordinal)
        {
            ["status"] = s => s.Automation_Tool_Status,
            ["switch_profile"] = s => s.Automation_Tool_SwitchProfile,
            ["set_engine"] = s => s.Automation_Tool_SetEngine,
            ["set_controllers_power"] = s => s.Automation_Tool_SetControllersPower,
            ["save_snapshot"] = s => s.Automation_Tool_SaveSnapshot,
            ["get_system"] = s => s.Automation_Tool_GetSystem,
            ["list_profiles"] = s => s.Automation_Tool_ListProfiles,
            ["get_profile"] = s => s.Automation_Tool_GetProfile,
            ["list_controllers"] = s => s.Automation_Tool_ListControllers,
            ["list_devices"] = s => s.Automation_Tool_ListDevices,
            ["get_device"] = s => s.Automation_Tool_GetDevice,
            ["wait_for_input"] = s => s.Automation_Tool_WaitForInput,
            ["get_mappings"] = s => s.Automation_Tool_GetMappings,
            ["list_macros"] = s => s.Automation_Tool_ListMacros,
            ["record_problem"] = s => s.Automation_Tool_RecordProblem,
            ["find_settings"] = s => s.Automation_Tool_FindSettings,
            ["get_setting"] = s => s.Automation_Tool_GetSetting,
            ["show"] = s => s.Automation_Tool_Show,
            ["add_device"] = s => s.Automation_Tool_AddDevice,
            ["set_device_inputs"] = s => s.Automation_Tool_SetDeviceInputs,
            ["remove_device"] = s => s.Automation_Tool_RemoveDevice,
            ["hold_control"] = s => s.Automation_Tool_HoldControl,
            ["set_setting"] = s => s.Automation_Tool_SetSetting,
            ["create_profile"] = s => s.Automation_Tool_CreateProfile,
            ["rename_profile"] = s => s.Automation_Tool_RenameProfile,
            ["delete_profile"] = s => s.Automation_Tool_DeleteProfile,
            ["import_profile"] = s => s.Automation_Tool_ImportProfile,
            ["export_profile"] = s => s.Automation_Tool_ExportProfile,
            ["add_controller"] = s => s.Automation_Tool_AddController,
            ["remove_controller"] = s => s.Automation_Tool_RemoveController,
            ["set_controller_type"] = s => s.Automation_Tool_SetControllerType,
            ["assign_device"] = s => s.Automation_Tool_AssignDevice,
            ["set_mappings"] = s => s.Automation_Tool_SetMappings,
            ["clear_mappings"] = s => s.Automation_Tool_ClearMappings,
            ["auto_map"] = s => s.Automation_Tool_AutoMap,
            ["save_macro"] = s => s.Automation_Tool_SaveMacro,
            ["delete_macro"] = s => s.Automation_Tool_DeleteMacro,
            ["undo"] = s => s.Automation_Tool_Undo,
            ["check_request"] = s => s.Automation_Tool_CheckRequest,
        };

        /// <summary>Whether every tool has its line, for a test.</summary>
        internal static bool HasPhrase(string tool) => Phrases.ContainsKey(tool);

        /// <summary>A tool's line in Activity, with what it named when it
        /// named something. A tool PadForge does not have is named as the
        /// program asked for it.</summary>
        internal static string Asked(string tool, string detail)
        {
            var s = Strings.Instance;
            string phrase = tool != null && Phrases.TryGetValue(tool, out var of)
                ? of(s)
                : string.Format(s.Automation_Tool_Unknown_Format, Cut(tool));
            if (string.IsNullOrWhiteSpace(detail)) return phrase;
            return string.Format(s.Automation_Tool_WithDetail_Format, phrase, Cut(detail));
        }

        private static string Cut(string text)
        {
            // A program's text, shown to the user: no format character
            // can turn it around or hide part of it.
            text = ShownText.Clean((text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ')).Trim();
            return text.Length > LongestDetail ? text.Substring(0, LongestDetail) + "…" : text;
        }

        // ── The request card's questions ──

        internal static string AskImport(string name) => string.Format(Strings.Instance.Automation_Ask_Import_Format, Cut(name));

        internal static string AskKeyboardMouse() => Strings.Instance.Automation_Ask_KeyboardMouse;

        internal static string KeyboardMouseLine() => Strings.Instance.Automation_Ask_KeyboardMouseLine;

        internal static string KeyLine(string target, string source)
            => string.Format(Strings.Instance.Automation_Ask_KeyRow_Format, Cut(target), Cut(source));

        internal static string AskMacro() => Strings.Instance.Automation_Ask_Macro;

        internal static string FileLine(string file) => string.Format(Strings.Instance.Automation_Ask_File_Format, Cut(file));

        internal static string RunsProgram(string program) => string.Format(Strings.Instance.Automation_Ask_Runs_Format, Cut(program));

        internal static string PressesKey(string key) => string.Format(Strings.Instance.Automation_Ask_Presses_Format, Cut(key));

        internal static string TypesText() => Strings.Instance.Automation_Ask_TypesText;

        internal static string MovesMouse() => Strings.Instance.Automation_Ask_MovesMouse;

        internal static string MakesKeyboardMouse() => Strings.Instance.Automation_Ask_MakesKeyboardMouse;

        internal static string TurnsOn(string what) => string.Format(Strings.Instance.Automation_Ask_TurnsOn_Format, Cut(what));

        internal static string AskSetting(string name) => string.Format(Strings.Instance.Automation_Ask_Setting_Format, Cut(name));

        internal static string SetsTo(string name, string value) => string.Format(Strings.Instance.Automation_Ask_SetsTo_Format, Cut(name), Cut(value));

        internal static string ListensOnNetwork() => Strings.Instance.Automation_Ask_Network;

        internal static string ChangesDrivers() => Strings.Instance.Automation_Ask_Drivers;

        internal static string ChangesUseOnly() => Strings.Instance.Automation_Ask_UseOnly;
    }
}
