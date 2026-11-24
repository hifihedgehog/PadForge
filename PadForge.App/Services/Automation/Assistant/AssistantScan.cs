using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// What a change would make PadForge do outside itself (#597), one line
    /// each for the request card: the programs a macro or a profile would run
    /// and the files and addresses it would open, the keys it would press,
    /// the text it would type and the mouse input it would send to Windows,
    /// a keyboard and mouse controller, and the extras it would turn on that
    /// listen on the network. An empty list asks nothing.
    ///
    /// <para>No function scanned a profile's or a macro's data for this
    /// before: the foreign importers record the programs they translate
    /// (ImportBuilder.Run), which a .pfprofile never passes through.</para>
    /// </summary>
    internal static class AssistantScan
    {
        /// <summary>The steps that send keys to Windows.</summary>
        private static readonly MacroActionType[] Keys =
        {
            MacroActionType.KeyPress, MacroActionType.KeyRelease, MacroActionType.ToggleKey, MacroActionType.RepeatKeyWhileHeld,
        };

        /// <summary>The steps that move the pointer, click or scroll in
        /// Windows.</summary>
        private static readonly MacroActionType[] Mouse =
        {
            MacroActionType.MouseMove, MacroActionType.MouseButtonPress, MacroActionType.MouseButtonRelease, MacroActionType.MouseScroll,
            MacroActionType.MouseRecenter, MacroActionType.MouseFixPosition, MacroActionType.MouseLimitRegion,
            MacroActionType.MoveMouseToScreenPosition, MacroActionType.MouseWheelTap, MacroActionType.MouseNudge,
            MacroActionType.ToggleMouseButton, MacroActionType.ToggleWheel,
        };

        /// <summary>What one macro would do outside PadForge.</summary>
        internal static List<string> Macro(MacroData macro)
        {
            var lines = new List<string>();
            if (macro?.Actions == null) return lines;
            foreach (var action in macro.Actions)
            {
                if (action == null) continue;
                string line = null;
                if (action.Type == MacroActionType.RunProgram)
                {
                    string program = (action.ProgramPath ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(action.ProgramArgs)) program += " " + action.ProgramArgs.Trim();
                    line = AssistantToolWords.RunsProgram(program.Length > 0 ? program : "?");
                }
                else if (Keys.Contains(action.Type))
                {
                    string key = !string.IsNullOrWhiteSpace(action.KeyString) ? action.KeyString
                        : action.KeyCode != 0 ? "0x" + action.KeyCode.ToString("X2", CultureInfo.InvariantCulture) : "?";
                    line = AssistantToolWords.PressesKey(key);
                }
                else if (action.Type == MacroActionType.TextBlock) line = AssistantToolWords.TypesText();
                else if (Mouse.Contains(action.Type)) line = AssistantToolWords.MovesMouse();
                if (line != null && !lines.Contains(line)) lines.Add(line);
            }
            return lines;
        }

        /// <summary>What a whole profile would do outside PadForge once it is
        /// in force.</summary>
        internal static List<string> Profile(ProfileData profile)
        {
            var lines = new List<string>();
            if (profile == null) return lines;
            void Add(string line)
            {
                if (line != null && !lines.Contains(line)) lines.Add(line);
            }
            foreach (var macro in profile.Macros ?? System.Array.Empty<MacroData>())
                foreach (string line in Macro(macro)) Add(line);
            if (profile.SlotCreated != null && profile.SlotControllerTypes != null)
            {
                for (int i = 0; i < profile.SlotCreated.Length && i < profile.SlotControllerTypes.Length; i++)
                    if (profile.SlotCreated[i] && (VirtualControllerType)profile.SlotControllerTypes[i] == VirtualControllerType.KeyboardMouse)
                        Add(AssistantToolWords.MakesKeyboardMouse());
            }
            var s = Strings.Instance;
            if (profile.EnableWebController) Add(AssistantToolWords.TurnsOn(s.Dashboard_WebController));
            if (profile.EnableDsuMotionServer) Add(AssistantToolWords.TurnsOn(s.Dashboard_MotionServer));
            if (profile.EnableGameModServer) Add(AssistantToolWords.TurnsOn(s.Dashboard_GameMods));
            if (profile.EnableGameTelemetry) Add(AssistantToolWords.TurnsOn(s.Dashboard_GameTelemetry));
            return lines;
        }
    }
}
