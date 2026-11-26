using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Services.Automation
{
    // ─────────────────────────────────────────────
    //  Arguments and results (#600)
    // ─────────────────────────────────────────────

    internal sealed class NoArguments { }

    internal sealed class StatusController
    {
        [Description("The controller's number. Tools take it.")]
        public int Number { get; set; }

        [Description("Its preset as its page names it.")]
        public string Name { get; set; }

        public string Type { get; set; }
        public bool On { get; set; }

        [Description("How many devices are assigned to it.")]
        public int Devices { get; set; }

        [Description("How many of those are connected.")]
        public int Online { get; set; }
    }

    internal sealed class StatusResult
    {
        [Description("True when PadForge answered. The copy an assistant starts says false while PadForge is closed or Automation is off.")]
        public bool Connected { get; set; } = true;

        public string PadforgeVersion { get; set; }

        [Description("commands, read, send_inputs or change_settings: what the user allows programs to do.")]
        public string AutomationLevel { get; set; }

        [Description("What the level allows, in a sentence.")]
        public string LevelAllows { get; set; }

        [Description("running, idle, stopping or stopped.")]
        public string Engine { get; set; }

        [Description("The profile in force, or none for the default profile.")]
        public OutsideText Profile { get; set; }

        public string ProfileId { get; set; }

        [Description("Whether a program switched to the profile, which holds it against switching by game.")]
        public bool ProfileHeld { get; set; }

        [Description("shown, minimized or tray.")]
        public string Window { get; set; }

        [Description("Whether the problem recorder records now.")]
        public bool Recording { get; set; }

        public List<StatusController> Controllers { get; set; } = new();

        [Description("What the user can do, when something stands in the way.")]
        public string Message { get; set; }

        [Description("The polling rate, the drivers and HidHide, at Read and above.")]
        public AssistantSystemResult System { get; set; }

        [Description("What each level above this connection's would add: its tools with their arguments. Only the user can raise the level.")]
        public List<AssistantLevelTools> HigherLevels { get; set; }

        [Description("While a program drives PadForge: that the key and mouse output programs feed is held back from windows that run with administrator rights, and the program of the window it was held from.")]
        public string HeldBack { get; set; }

        [Description("When Continue Polling When Window Loses Focus is off: that the engine stops while PadForge is not the program in front.")]
        public string EngineBehind { get; set; }
    }

    /// <summary>One level's tools, for status (#600).</summary>
    internal sealed class AssistantLevelTools
    {
        [Description("commands, read, send_inputs or change_settings.")]
        public string Level { get; set; }

        public List<AssistantToolSummary> Tools { get; set; } = new();
    }

    /// <summary>A tool by its name, title and arguments.</summary>
    internal sealed class AssistantToolSummary
    {
        public string Name { get; set; }
        public string Title { get; set; }

        [Description("Its arguments, a required one marked with *.")]
        public List<string> Arguments { get; set; } = new();
    }

    internal sealed class SwitchProfileArgs
    {
        [Description("The profile's ID or name. Leave it out to let go of the profile a program held and return to the default profile.")]
        public string Profile { get; set; }
    }

    internal sealed class SwitchProfileResult
    {
        [Description("The profile in force now, or none for the default profile.")]
        public OutsideText Profile { get; set; }

        [Description("Whether PadForge holds it against switching by game.")]
        public bool Held { get; set; }
    }

    internal sealed class OnOffArgs
    {
        [JsonRequired, Description("True to start or turn on, false to stop or turn off.")]
        public bool On { get; set; }
    }

    internal sealed class ControllersPowerArgs
    {
        [JsonRequired, Description("True to turn on, false to turn off.")]
        public bool On { get; set; }

        [Description("One controller's number. Leave it out for every controller.")]
        public int? Controller { get; set; }
    }

    internal sealed class DoneResult
    {
        [Description("What happened, in a sentence.")]
        public string Message { get; set; }
    }

    internal sealed class FileResult
    {
        [Description("The file written, by name.")]
        public string File { get; set; }

        [Description("The file's bytes in base64, when no path was given to write it to.")]
        public string ContentBase64 { get; set; }

        public string Message { get; set; }
    }

    internal sealed class ListProfilesArgs
    {
        [Description("A profile's ID or name, to add what that profile holds: its controllers, devices, games, polling rate and extras.")]
        public string Profile { get; set; }
    }

    internal sealed class ProfilesResult
    {
        public List<AssistantProfile> Profiles { get; set; } = new();

        [Description("What the profile asked for holds, when one was.")]
        public AssistantProfileDetail Profile { get; set; }
    }

    internal sealed class ProfileArgs
    {
        [JsonRequired, Description("The profile's ID or name.")]
        public string Profile { get; set; }
    }

    internal sealed class ListControllersArgs
    {
        [Description("True to add what each controller sends to the game now.")]
        public bool Outputs { get; set; }

        [Description("True to add the presets each type offers, which add_controller and set_controller_type take.")]
        public bool Presets { get; set; }
    }

    internal sealed class ControllersResult
    {
        public List<AssistantController> Controllers { get; set; } = new();

        [Description("The presets of each type, when asked for.")]
        public Dictionary<string, List<AssistantPreset>> Presets { get; set; }

        [Description("The revision of which device is on which controller. assign_device takes it, and refuses a change made against an older one.")]
        public long Revision { get; set; }
    }

    internal sealed class ListDevicesArgs
    {
        [Description("Only devices of this kind: gamepad, joystick, wheel, keyboard, mouse, midi, touchpad, program and the other kinds list_devices names.")]
        public string Kind { get; set; }

        [Description("True for only the devices assigned to a controller, false for only the others.")]
        public bool? Assigned { get; set; }

        [Description("True for only connected devices.")]
        public bool? Online { get; set; }

        [Description("Only devices whose name holds these words.")]
        public string Words { get; set; }
    }

    internal sealed class DeviceSummary
    {
        [Description("The device's short ID, the same in every run. Tools take it.")]
        public string Id { get; set; }

        [Description("The name its product gives.")]
        public OutsideText Name { get; set; }

        public string Kind { get; set; }
        public bool Online { get; set; }
        public int Buttons { get; set; }
        public int Axes { get; set; }
        public int Sliders { get; set; }
        public int Hats { get; set; }
        public bool Gyro { get; set; }
        public bool Touchpad { get; set; }

        [Description("Whether it rumbles.")]
        public bool Rumble { get; set; }

        [Description("The numbers of the controllers it is assigned to.")]
        public List<int> Controllers { get; set; } = new();

        [Description("Whether HidHide hides it from games.")]
        public bool Hidden { get; set; }

        [Description("Whether a program added it.")]
        public bool FromProgram { get; set; }

        [Description("Its maker, as the device names itself, when it does.")]
        public OutsideText Maker { get; set; }

        [Description("The IDs of the device's own settings and actions that a plugin declares, which read, set and press take.")]
        public List<string> Settings { get; set; }
    }

    internal sealed class DevicesResult
    {
        public List<DeviceSummary> Devices { get; set; } = new();
        public long Revision { get; set; }
    }

    internal sealed class DeviceArgs
    {
        [JsonRequired, Description("The device's short ID, from list_devices.")]
        public string Device { get; set; }

        [Description("True to add the device's state now.")]
        public bool Live { get; set; }
    }

    internal sealed class DeviceControl
    {
        [Description("button, axis, slider or hat.")]
        public string Kind { get; set; }

        public int Index { get; set; }

        [Description("The control's name on the device.")]
        public OutsideText Name { get; set; }

        [Description("Where an axis rests, when PadForge knows it: 0 for a stick, a trigger's own rest from 0 to 100.")]
        public double? Rest { get; set; }

        [Description("For an axis: stick (-100 to 100) or trigger (0 to 100).")]
        public string Range { get; set; }

        [Description("The input as set_mappings takes it: Button 3, Axis 1, Slider 0, POV 0 Up.")]
        public string Input { get; set; }
    }

    internal sealed class DeviceState
    {
        [Description("The indexes of the buttons held down.")]
        public List<int> ButtonsDown { get; set; } = new();

        [Description("Every axis: a stick -100 to 100 with 0 at rest, a trigger 0 to 100.")]
        public List<double> Axes { get; set; } = new();

        [Description("Every slider, 0 to 100.")]
        public List<double> Sliders { get; set; } = new();

        [Description("Every hat in degrees clockwise from up, or -1 at rest.")]
        public List<double> Hats { get; set; } = new();

        [Description("Every hat's directions: Up, Right, Down, Left, two for a diagonal, empty at rest.")]
        public List<string> HatDirections { get; set; } = new();

        [Description("The battery in percent, or -1 when the device has none or says nothing.")]
        public int Battery { get; set; }

        public bool Charging { get; set; }
    }

    internal sealed class DeviceDetail
    {
        public string Id { get; set; }
        public OutsideText Name { get; set; }
        public string Kind { get; set; }
        public bool Online { get; set; }
        public List<DeviceControl> Controls { get; set; } = new();

        [Description("What else it has: gyro, accel, touchpad, battery.")]
        public List<string> Features { get; set; } = new();

        [Description("The feedback it takes: rumble, trigger rumble, trigger effects, light, player number.")]
        public List<string> Feedback { get; set; } = new();

        public List<int> Controllers { get; set; } = new();

        [Description("Its state now, when asked for and the device is connected.")]
        public DeviceState State { get; set; }
    }

    internal sealed class WaitForInputArgs
    {
        [Description("How long to wait, 1 to 60 seconds. 15 when left out.")]
        public int? Seconds { get; set; }

        [Description("Only this device's short ID. Every connected device when left out.")]
        public string Device { get; set; }

        [Description("True to record every input change for the whole time, as a timeline, in place of the first change.")]
        public bool Timeline { get; set; }
    }

    /// <summary>One change of a timeline (#600).</summary>
    internal sealed class InputChange
    {
        [Description("Milliseconds since the wait began.")]
        public long Ms { get; set; }

        public string Device { get; set; }

        [Description("The input as set_mappings takes it.")]
        public string Input { get; set; }

        [Description("Its value in plain units: 1 or 0 for a button, -100 to 100 for a stick, 0 to 100 for a trigger or slider, degrees or -1 for a hat.")]
        public double Value { get; set; }
    }

    internal sealed class InputResult
    {
        [Description("True when the user moved something in time.")]
        public bool Found { get; set; }

        public string Device { get; set; }
        public OutsideText DeviceName { get; set; }

        [Description("button, axis, slider or hat.")]
        public string Kind { get; set; }

        public int Index { get; set; }
        public OutsideText Name { get; set; }

        [Description("The input as set_mappings takes it.")]
        public string Input { get; set; }

        [Description("The value it moved to: 1 for a button, -100 to 100 for a stick, 0 to 100 for a trigger or slider, degrees for a hat.")]
        public double Value { get; set; }

        public string Message { get; set; }

        [Description("With timeline: every change, in order.")]
        public List<InputChange> Timeline { get; set; }

        [Description("With timeline: changes left out past the most a timeline holds.")]
        public int Dropped { get; set; }

        [Description("With timeline: engine passes that went by between two reads, whose changes may be missing. 0 when every pass was read.")]
        public long PassesUnread { get; set; }
    }

    internal sealed class ControllerArgs
    {
        [JsonRequired, Description("The controller's number, from list_controllers.")]
        public int Controller { get; set; }
    }

    internal sealed class GetMappingsArgs
    {
        [JsonRequired, Description("The controller's number, from list_controllers.")]
        public int Controller { get; set; }

        [Description("True to add the controller's macros, each with its trigger, steps and data, and its menus.")]
        public bool Macros { get; set; }
    }

    internal sealed class MappingsResult
    {
        public int Controller { get; set; }
        public List<AssistantMappingRow> Rows { get; set; } = new();

        [Description("The macros, when asked for. save_macro takes a macro's data.")]
        public List<AssistantMacro> Macros { get; set; }

        [Description("The menus, when asked for.")]
        public List<AssistantMenu> Menus { get; set; }
    }

    internal sealed class RecordArgs
    {
        [Description("How long to record, 3 to 60 seconds, 10 when left out, which needs the Read level. 0 saves a diagnostics snapshot instead, at any level. Ask the user to show the problem meanwhile.")]
        public int? Seconds { get; set; }

        [Description("A note PadForge marks the recording with at its start.")]
        public string Note { get; set; }
    }

    internal sealed class RecordResult
    {
        [Description("What the recorder found, scrubbed of private values.")]
        public string Summary { get; set; }

        [Description("Where the recording stands: it waits in PadForge for the user to look over and save.")]
        public string Message { get; set; }
    }

    internal sealed class FindArgs
    {
        [Description("Words from the place's name, help or where it is, in English or the interface language. Leave them out with a kind to list that kind.")]
        public string Words { get; set; }

        [Description("Only places of this kind: setting, action, card, section, part, extra, page, dialog, menu_item, device, controller, profile, macro, plugin or plugin_setting.")]
        public string Kind { get; set; }

        [Description("How many to return, 1 to 100. 15 when left out.")]
        public int? Most { get; set; }
    }

    internal sealed class IdArgs
    {
        [JsonRequired, Description("The place's ID, from find, where or another read.")]
        public string Id { get; set; }
    }

    internal sealed class SetArgs
    {
        [Description("The place's ID, from find or read.")]
        public string Id { get; set; }

        [Description("The new value: a number, true or false, a choice's text or text, as the place's takes says. A section, part or extra takes true, which opens it.")]
        public JsonElement? Value { get; set; }

        [Description("In place of id and value: several places by ID, each with its value, set in order. The first refused stops the rest.")]
        public Dictionary<string, JsonElement> Values { get; set; }
    }

    internal sealed class SetResult : AssistantChangeResult
    {
        [Description("Each place set, as it reads after.")]
        public List<AssistantPlace> Places { get; set; } = new();
    }

    internal sealed class PressResult : AssistantChangeResult
    {
        public AssistantPlace Place { get; set; }

        [Description("The dialog the press opened, with its text and buttons, when it opened one.")]
        public AssistantRead Dialog { get; set; }

        [Description("Set when the press opened a Windows dialog PadForge cannot read: it waits for the person.")]
        public string WaitingForPerson { get; set; }
    }

    internal sealed class ShowStepArgs
    {
        [JsonRequired, Description("The place's ID, from find: a setting, action, card, section, extra, page, dialog, device, controller, profile, macro, plugin or a plugin's setting.")]
        public string Place { get; set; }

        [Description("Your words for the user, beside the place. PadForge heads them with your name, takes out control characters and cuts them at 300 characters.")]
        public string Words { get; set; }

        [Description("How the step goes on: click (when the user clicks the place, the default), seconds, or next (at your next call).")]
        public AssistantAdvance? Advance { get; set; }

        [Description("With advance seconds: 1 to 60. 5 when left out.")]
        public int? Seconds { get; set; }
    }

    internal sealed class ShowArgs
    {
        [Description("One place's ID. Leave place and steps out to take the callout away.")]
        public string Place { get; set; }

        [Description("Your words for the user, beside the place.")]
        public string Words { get; set; }

        [Description("How it goes on: click (the default), seconds or next.")]
        public AssistantAdvance? Advance { get; set; }

        [Description("With advance seconds: 1 to 60. 5 when left out.")]
        public int? Seconds { get; set; }

        [Description("In place of place: several steps, shown one after another.")]
        public List<ShowStepArgs> Steps { get; set; }

        [Description("True to ring each click on the way to the last place first: the sidebar entry, the section or extra, a filter that hides the row, the row, and each closed card and More. The user does the clicking.")]
        public bool Walk { get; set; }
    }

    internal sealed class PlacesResult
    {
        public List<AssistantPlace> Places { get; set; } = new();
    }

    internal sealed class SetValueArgs
    {
        [JsonRequired, Description("The value's name, as a Custom Expression trigger reads it: a letter, then up to 31 letters, digits, _ . or -.")]
        public string Name { get; set; }

        [Description("The number to hand to the triggers that read the name. Leave it out with release.")]
        public double? Value { get; set; }

        [Description("True to let go of the value this connection set, which then reads 0 unless another program set it since.")]
        public bool Release { get; set; }
    }

    internal sealed class AxisDeclaration
    {
        public string Name { get; set; }

        [Description("Where it rests, as a share of its travel, 0 to 100. 50 makes a stick, read -100 to 100. Left out, an axis rests at 50 and a slider at 0.")]
        public double? Rest { get; set; }
    }

    internal enum AssistantFeedback { Rumble, TriggerRumble, Light, Player, TriggerEffects }

    internal sealed class AddDeviceArgs
    {
        [JsonRequired, Description("A key of the program's own for the device, 1 to 64 letters, digits, _ . or -. The same key gives the same device again.")]
        public string Key { get; set; }

        [Description("The device's name on the Devices page.")]
        public string Name { get; set; }

        [Description("The buttons' names, up to 256.")]
        public List<string> Buttons { get; set; }

        [Description("The axes, up to 24.")]
        public List<AxisDeclaration> Axes { get; set; }

        [Description("The sliders, up to 8.")]
        public List<AxisDeclaration> Sliders { get; set; }

        [Description("The hats' names, up to 4.")]
        public List<string> Hats { get; set; }

        public bool Gyro { get; set; }
        public bool Accel { get; set; }
        public bool Battery { get; set; }

        [Description("Each touchpad's finger count, up to 4 touchpads.")]
        public List<int> Touchpads { get; set; }

        [Description("The feedback a game may send it.")]
        public List<AssistantFeedback> Feedback { get; set; }
    }

    internal sealed class AddDeviceResult
    {
        [Description("The device's short ID.")]
        public string Device { get; set; }

        public string Message { get; set; }
    }

    internal sealed class SetDeviceInputsArgs
    {
        [JsonRequired, Description("The short ID of a device this connection added.")]
        public string Device { get; set; }

        [Description("Buttons by index, true for down.")]
        public Dictionary<string, bool> Buttons { get; set; }

        [Description("Axes and sliders by index: a stick -100 to 100 with 0 at rest, a trigger or slider 0 to 100. Sliders are 24 to 31.")]
        public Dictionary<string, double> Axes { get; set; }

        [Description("Hats by index, in degrees clockwise from up, or -1 at rest.")]
        public Dictionary<string, double> Hats { get; set; }

        [Description("Hats by index as directions: Up, Right, Down, Left, or two joined by +, such as Up+Right. Empty for rest.")]
        public Dictionary<string, string> HatDirections { get; set; }

        [Description("True puts every control at rest first, before the others apply.")]
        public bool Rest { get; set; }
    }

    internal sealed class DeviceOnlyArgs
    {
        [JsonRequired, Description("The device's short ID.")]
        public string Device { get; set; }
    }

    internal sealed class HoldArgs
    {
        [JsonRequired, Description("The short ID of any device.")]
        public string Device { get; set; }

        [JsonRequired, Description("The control: b and a button's index (b3), a and an axis's (a0, sliders a24 to a31), h and a hat's (h0).")]
        public string Control { get; set; }

        [Description("The value to hold it at: true or false for a button, -100 to 100 for a stick or 0 to 100 for a trigger or slider, degrees or -1 for a hat, or a hat's directions such as \"Up+Right\". Leave it out with release.")]
        public JsonElement? Value { get; set; }

        [Description("True to let go of the control, which then reads the device again.")]
        public bool Release { get; set; }
    }

    internal sealed class CreateProfileArgs
    {
        [JsonRequired, Description("The new profile's name.")]
        public string Name { get; set; }

        [Description("A profile's ID or name to copy. An empty profile when left out.")]
        public string CopyOf { get; set; }
    }

    internal sealed class ProfileChangeResult : AssistantChangeResult
    {
        public string ProfileId { get; set; }
        public OutsideText Name { get; set; }
    }

    internal sealed class RenameProfileArgs
    {
        [JsonRequired, Description("The profile's ID or name.")]
        public string Profile { get; set; }

        [JsonRequired, Description("Its new name.")]
        public string Name { get; set; }
    }

    internal sealed class ImportProfileArgs
    {
        [Description("The full path of a .pfprofile file, or of a profile another controller program saved. The file is opened with the user's own rights, by the copy of PadForge an assistant starts.")]
        public string Path { get; set; }

        [Description("In place of path: the file's name, which says what kind of file it is.")]
        public string FileName { get; set; }

        [Description("In place of path: the file's bytes in base64. A script's call line runs to 65,536 bytes, which holds a file of about 48 KB in base64.")]
        public string ContentBase64 { get; set; }
    }

    internal sealed class ExportProfileArgs
    {
        [JsonRequired, Description("The profile's ID or name.")]
        public string Profile { get; set; }

        [Description("The full path of the .pfprofile file to write, with the user's own rights. Leave it out to get the file's bytes in the result.")]
        public string Path { get; set; }
    }

    internal sealed class AddControllerArgs
    {
        [JsonRequired, Description("xbox, playstation, nintendo, extended, keyboard_mouse, midi or vr.")]
        public string Type { get; set; }

        [Description("A preset's ID from list_controllers with presets. The type's first when left out.")]
        public string Preset { get; set; }
    }

    internal sealed class ControllerChangeResult : AssistantChangeResult
    {
        public int Controller { get; set; }
    }

    internal sealed class SetControllerTypeArgs
    {
        [JsonRequired, Description("The controller's number.")]
        public int Controller { get; set; }

        [JsonRequired, Description("xbox, playstation, nintendo, extended, keyboard_mouse, midi or vr.")]
        public string Type { get; set; }

        [Description("A preset's ID. The type's first when left out.")]
        public string Preset { get; set; }
    }

    internal sealed class AssignDeviceArgs
    {
        [JsonRequired, Description("The device's short ID.")]
        public string Device { get; set; }

        [JsonRequired, Description("The controller's number.")]
        public int Controller { get; set; }

        [Description("False to take the device off the controller. True when left out.")]
        public bool? Assigned { get; set; }

        [JsonRequired, Description("The revision list_devices or list_controllers gave. A change made against an older one is refused, so read again.")]
        public long Revision { get; set; }
    }

    internal sealed class MappingEditArgs
    {
        [JsonRequired, Description("The row's target, from get_mappings.")]
        public string Target { get; set; }

        [Description("The input, as get_device's inputs name it: Button 3, Axis 1, Slider 0, POV 0 Up. Empty clears the row.")]
        public string Source { get; set; }

        [Description("The short ID of the device the input belongs to. Leave it out for any device on the controller.")]
        public string Device { get; set; }

        [Description("The input that drives the other direction of an axis row.")]
        public string NegativeSource { get; set; }

        public bool? Inverted { get; set; }
        public bool? HalfAxis { get; set; }

        [Description("The dead zone in percent, 0 to 100.")]
        public int? DeadZone { get; set; }
    }

    internal sealed class SetMappingsArgs
    {
        [JsonRequired, Description("The controller's number.")]
        public int Controller { get; set; }

        [Description("The rows to write. Rows not named stay as they are. A row with an empty source is cleared.")]
        public List<MappingEditArgs> Rows { get; set; }

        [Description("True to clear every row first, before the rows given are written.")]
        public bool ClearAll { get; set; }
    }

    internal sealed class MappingsChangeResult : AssistantChangeResult
    {
        public int Controller { get; set; }
        public List<AssistantMappingRow> Rows { get; set; }
    }

    internal sealed class SaveMacroArgs
    {
        [JsonRequired, Description("The controller's number.")]
        public int Controller { get; set; }

        [Description("The index of the macro to replace, from get_mappings with macros. A new macro when left out.")]
        public int? Index { get; set; }

        [JsonRequired, Description("The macro in PadForge's own JSON form, as get_mappings with macros gives a macro's data.")]
        public JsonElement Macro { get; set; }
    }

    internal sealed class MacroChangeResult : AssistantChangeResult
    {
        public int Controller { get; set; }
        public int Index { get; set; }
    }

    internal sealed class DeleteMacroArgs
    {
        [JsonRequired, Description("The controller's number.")]
        public int Controller { get; set; }

        [JsonRequired, Description("The macro's index, from get_mappings with macros.")]
        public int Index { get; set; }
    }

    internal sealed class UndoResult : AssistantChangeResult
    {
        [Description("What was put back.")]
        public string PutBack { get; set; }
    }

    internal sealed class CheckRequestArgs
    {
        [JsonRequired, Description("The pending_request a change returned.")]
        public int RequestId { get; set; }
    }

    // ─────────────────────────────────────────────
    //  The tools
    // ─────────────────────────────────────────────

    /// <summary>What a tool does for a program held to its own devices and
    /// the ones picked (#600 DESIGN-2 N3).</summary>
    internal enum ReachVerdict
    {
        /// <summary>It reads, and works whole.</summary>
        Whole,

        /// <summary>It drives a device, one the reach takes.</summary>
        ItsDevices,

        /// <summary>It sets or presses an entry of a device the reach
        /// takes.</summary>
        EntriesOfItsDevices,

        /// <summary>It acts on the rest of PadForge, and is refused.</summary>
        Refused,
    }

    /// <summary>
    /// PadForge's tools for assistants (#600), in the order every list keeps,
    /// by level: Commands, Read, Send Inputs, Change Settings. Each one sets
    /// all four hints, takes typed arguments and returns a typed result. A
    /// command goes through the core's executor, the one that answers the
    /// control pipe, a device of a program's own through the scripts' line
    /// verbs, and every change through the calls the pages make.
    /// </summary>
    internal static class AssistantTools
    {
        internal static readonly IReadOnlyList<AssistantToolDefinition> All = Build();

        private static AssistantToolDefinition Tool<TArgs, TResult>(string name, string title, string description,
            AutomationLevel level, bool readOnly, bool destructive, bool idempotent,
            Func<AssistantInvocation, TArgs, CancellationToken, Task<TResult>> run, bool changes = false)
            => new()
            {
                Name = name,
                Title = title,
                Description = description,
                Level = level,
                ReadOnly = readOnly,
                Destructive = destructive,
                Idempotent = idempotent,
                // Every tool acts on PadForge and this PC alone.
                OpenWorld = false,
                Arguments = typeof(TArgs),
                Result = typeof(TResult),
                Changes = changes,
                Run = async (inv, args, ct) => await run(inv, (TArgs)args, ct).ConfigureAwait(false),
            };

        /// <summary>
        /// What a tool does for a program held to its own devices and the
        /// ones the user picked (#600 DESIGN-2 N3). A tool that only reads
        /// works whole: the reach never limits what a program reads. The
        /// tools that drive a device work on the devices the reach takes,
        /// which the pipe checks for each line. set and press work on an
        /// entry of such a device alone. Every other tool is refused, the
        /// Commands tools among them. A tool added later is refused until it
        /// is named here.
        /// </summary>
        internal static ReachVerdict UnderReach(AssistantToolDefinition tool) => tool.Name switch
        {
            "add_device" or "set_device_inputs" or "remove_device" or "hold_control" => ReachVerdict.ItsDevices,
            "set" or "press" => ReachVerdict.EntriesOfItsDevices,
            _ => tool.ReadOnly ? ReachVerdict.Whole : ReachVerdict.Refused,
        };

        /// <summary>Refuses a set or a press of an entry whose device the
        /// caller's reach does not take.</summary>
        private static void CheckReach(AssistantInvocation inv, string id)
        {
            var session = inv.Caller.Session;
            if (session?.Reach == null) return;
            Guid? device = inv.OnUi(() => inv.Catalog.DeviceHolding(id));
            if (device is not Guid held || !inv.Host.Sessions.ReachTakes(session, held))
            {
                // Its own ask holds it while the user kept all of PadForge
                // for it (#600 DESIGN-2 N3), and the words say so.
                if (session.HeldByItsAsk)
                    throw new AssistantRefusal("outside-ask", "This program asked to drive and change only its own devices, and "
                        + id + " belongs to none of them. It still reads every device. Without that ask it works within the reach the user set, on PadForge's Automation page.");
                throw new AssistantRefusal("outside-reach", "This program's reach is its own devices and the ones the user picked, and "
                    + id + " belongs to none of them. It still reads every device. Only the user can widen its reach, on PadForge's Automation page.");
            }
        }

        private static IReadOnlyList<AssistantToolDefinition> Build() => new[]
        {
            // ── Commands ──
            Tool<NoArguments, StatusResult>("status", "PadForge's Status",
                "Says what PadForge is doing: the engine, the profile in force, the window, each virtual controller, what this connection's level allows, and at Read the polling rate and drivers. It lists what each higher level would add, with its arguments. It says while key and mouse output a program feeds is held back from windows with administrator rights, and when the engine stops while PadForge is behind. Call it first, and again when a call is refused.",
                AutomationLevel.Commands, true, false, true, Status),
            Tool<SwitchProfileArgs, SwitchProfileResult>("switch_profile", "Switch Profile",
                "Puts a profile in force and holds it against switching by game, or with no profile lets go and returns to the default profile.",
                AutomationLevel.Commands, false, false, true, SwitchProfile),
            Tool<OnOffArgs, DoneResult>("set_engine", "Start or Stop the Engine",
                "Starts or stops the engine that reads the devices and drives the virtual controllers. Games lose their virtual controllers while it is stopped.",
                AutomationLevel.Commands, false, false, true, SetEngine),
            Tool<ControllersPowerArgs, DoneResult>("set_controllers_power", "Turn Virtual Controllers On or Off",
                "Turns every virtual controller, or one by its number, on or off. A controller that is off is gone from games until it is turned on.",
                AutomationLevel.Commands, false, false, true, SetControllersPower),
            Tool<RecordArgs, RecordResult>("record_problem", "Record a Problem",
                "Records what the devices and virtual controllers do for a few seconds while the user shows the problem, and returns what the recorder found. The recording waits in PadForge for the user to review and save. With seconds 0 it saves a diagnostics snapshot instead.",
                AutomationLevel.Commands, false, false, false, RecordProblem),

            // ── Read ──
            Tool<ListProfilesArgs, ProfilesResult>("list_profiles", "List Profiles",
                "Lists the profiles with their IDs, names, games and which one is in force, and with a profile what it holds: its controllers with their devices, its games, its polling rate and the extras it turns on.",
                AutomationLevel.Read, true, false, true, ListProfiles),
            Tool<ListControllersArgs, ControllersResult>("list_controllers", "List Virtual Controllers",
                "Lists the virtual controllers games see, with their numbers, types, presets and devices, and on request what each sends to the game now and the presets of each type.",
                AutomationLevel.Read, true, false, true, ListControllers),
            Tool<ListDevicesArgs, DevicesResult>("list_devices", "List Devices",
                "Lists the input devices PadForge reads, keyboards and mice included, each by product name, maker and a short ID, with a filter by kind, assignment, connection or words.",
                AutomationLevel.Read, true, false, true, (inv, a, _) => Task.FromResult(ListDevices(inv, a))),
            Tool<DeviceArgs, DeviceDetail>("get_device", "A Device's Controls",
                "Reads one device: each button, axis, slider and hat by index and name with the input set_mappings takes for it, its features and feedback, and on request its state now.",
                AutomationLevel.Read, true, false, true, (inv, a, _) => Task.FromResult(GetDevice(inv, a))),
            Tool<WaitForInputArgs, InputResult>("wait_for_input", "Wait for the User's Input",
                "Waits for the next button, hat or axis the user moves on any device, or on one, and says what it was. With timeline it records every change for the whole time instead. Ask the user to press the control they mean, then call this.",
                AutomationLevel.Read, true, false, false, WaitForInput),
            Tool<GetMappingsArgs, MappingsResult>("get_mappings", "A Controller's Mappings",
                "Reads every mapping row of a virtual controller: its target, the input that feeds it, the device and the row's options, and on request its macros and menus.",
                AutomationLevel.Read, true, false, true, GetMappings),
            Tool<FindArgs, AssistantFound>("find", "Find in PadForge",
                "Finds anything on PadForge's pages, dialogs and menus by words, or every place of a kind: settings, buttons, cards, sections, extras, pages, dialogs, menu items, devices, controllers, profiles, macros, plugins and what plugins declare. Each comes with its ID, words, place, kind, value, range or choices, whether it is turned on and shown, and its class. It says how many it left out.",
                AutomationLevel.Read, true, false, true, (inv, a, _) =>
                {
                    inv.Detail = a.Words ?? a.Kind;
                    int most = Math.Clamp(a.Most ?? 15, 1, 100);
                    return Task.FromResult(inv.OnUi(() => inv.Catalog.Find(a.Words, a.Kind, most)));
                }),
            Tool<IdArgs, AssistantRead>("read", "Read a Place",
                "Reads one place by its ID: a setting's value, kind and range, or a page, card, section, dialog, device, controller or plugin with every label, value, status line, list row and button under it. It changes nothing on screen.",
                AutomationLevel.Read, true, false, true, (inv, a, _) => { inv.Detail = a.Id; return Task.FromResult(inv.OnUi(() => inv.Catalog.Read(a.Id))); }),
            Tool<NoArguments, AssistantWhere>("where", "What the User Sees",
                "Says what the user sees now: where PadForge's window is, the page and section, the dialogs open, a Windows dialog that waits for the person, the controller, device and profile picked, and your callout's step.",
                AutomationLevel.Read, true, false, true, (inv, _, _) => Task.FromResult(inv.OnUi(() => inv.Catalog.Where()))),
            Tool<ShowArgs, AssistantShown>("show", "Show the User Where",
                "Rings a place in PadForge with your words beside it, under your name, or walks the user through several, one step at a time. It never clicks for the user and never brings PadForge's window up: when the window is not in front, PadForge's question card asks the user, and the steps play once they pick Show. Show with no place takes your callout away.",
                AutomationLevel.Read, true, false, false, Show),

            // ── Send Inputs ──
            Tool<AddDeviceArgs, AddDeviceResult>("add_device", "Add a Device of Your Own",
                "Adds a device this connection drives, with the buttons, axes, sliders and hats named. It shows on the Devices page and is assigned and mapped like any device. It goes offline when the connection ends.",
                AutomationLevel.SendInputs, false, false, true, AddDevice),
            Tool<SetDeviceInputsArgs, DoneResult>("set_device_inputs", "Drive Your Device",
                "Sets controls of a device this connection added, as one change.",
                AutomationLevel.SendInputs, false, false, true, SetDeviceInputs),
            Tool<DeviceOnlyArgs, DoneResult>("remove_device", "Remove Your Device",
                "Takes a device this connection added offline, with its outputs at rest.",
                AutomationLevel.SendInputs, false, true, true, RemoveDevice),
            Tool<HoldArgs, DoneResult>("hold_control", "Hold or Release a Control",
                "Holds a control of any device at a value, as if the user held it, until it is released or the connection ends.",
                AutomationLevel.SendInputs, false, false, true, HoldControl),
            Tool<SetValueArgs, DoneResult>("set_value", "Hand a Value to Macro Triggers",
                "Sets a named value that Custom Expression macro triggers read, until it is released or the connection ends.",
                AutomationLevel.SendInputs, false, false, true, SetValue),

            // ── Change Settings ──
            Tool<SetArgs, SetResult>("set", "Change a Place",
                "Changes a setting, text box, list or choice by its ID through the control on its page, so it is checked and saved as a person's change is, or opens a section. A value out of range is refused with the range, and a control the page has turned off is refused. Some changes ask the user first.",
                AutomationLevel.ChangeSettings, false, true, true, Set, changes: true),
            Tool<IdArgs, PressResult>("press", "Press a Button",
                "Presses a button, a menu item or an open dialog's button by its ID, as a person's click does. A press that opens a dialog answers with the dialog's text and buttons. Some presses ask the user first, and some buttons are the user's alone.",
                AutomationLevel.ChangeSettings, false, true, false, Press, changes: true),
            Tool<CreateProfileArgs, ProfileChangeResult>("create_profile", "Create a Profile",
                "Makes a profile, empty or a copy of another. It does not put it in force.",
                AutomationLevel.ChangeSettings, false, false, false, CreateProfile, changes: true),
            Tool<RenameProfileArgs, ProfileChangeResult>("rename_profile", "Rename a Profile",
                "Gives a profile a new name.",
                AutomationLevel.ChangeSettings, false, true, true, RenameProfile, changes: true),
            Tool<ProfileArgs, ProfileChangeResult>("delete_profile", "Delete a Profile",
                "Deletes a profile. The default profile cannot be deleted. Undo puts it back.",
                AutomationLevel.ChangeSettings, false, true, true, DeleteProfile, changes: true),
            Tool<ImportProfileArgs, ProfileChangeResult>("import_profile", "Import a Profile",
                "Adds a profile from a .pfprofile file or another controller program's file. PadForge always asks the user, naming the programs it would run and the keys it would press.",
                AutomationLevel.ChangeSettings, false, false, false, ImportProfile, changes: true),
            Tool<ExportProfileArgs, FileResult>("export_profile", "Export a Profile",
                "Writes a profile to a .pfprofile file that PadForge and other people can import.",
                AutomationLevel.ChangeSettings, false, true, true, ExportProfile),
            Tool<AddControllerArgs, ControllerChangeResult>("add_controller", "Add a Virtual Controller",
                "Makes a virtual controller of a type and preset. Games see it a few seconds later while the engine runs. A keyboard and mouse controller asks the user first.",
                AutomationLevel.ChangeSettings, false, false, false, AddController, changes: true),
            Tool<ControllerArgs, ControllerChangeResult>("remove_controller", "Remove a Virtual Controller",
                "Deletes a virtual controller with its mappings and macros. Undo puts it back.",
                AutomationLevel.ChangeSettings, false, true, true, RemoveController, changes: true),
            Tool<SetControllerTypeArgs, ControllerChangeResult>("set_controller_type", "Change a Controller's Type",
                "Changes a virtual controller's type and preset, and maps its devices again for the new type.",
                AutomationLevel.ChangeSettings, false, true, true, SetControllerType, changes: true),
            Tool<AssignDeviceArgs, ControllerChangeResult>("assign_device", "Assign a Device",
                "Assigns a device to a virtual controller, which maps it there, or takes it off. Assigning to a keyboard and mouse controller asks the user first.",
                AutomationLevel.ChangeSettings, false, true, true, AssignDevice, changes: true),
            Tool<SetMappingsArgs, MappingsChangeResult>("set_mappings", "Set Mapping Rows",
                "Writes mapping rows of a virtual controller: which input of which device feeds each target, and the row's options. Rows not named stay, a row with an empty source is cleared, and clear_all clears every row first. On a keyboard and mouse controller the user is asked first.",
                AutomationLevel.ChangeSettings, false, true, true, SetMappings, changes: true),
            Tool<ControllerArgs, MappingsChangeResult>("auto_map", "Map Automatically",
                "Maps a virtual controller's devices again as assigning them does, the way PadForge maps a known gamepad.",
                AutomationLevel.ChangeSettings, false, true, true, AutoMap, changes: true),
            Tool<SaveMacroArgs, MacroChangeResult>("save_macro", "Save a Macro",
                "Adds a macro to a virtual controller or replaces one, from PadForge's own JSON form. A macro that runs a program, opens a file or an address, or presses keys or moves the mouse asks the user first.",
                AutomationLevel.ChangeSettings, false, true, true, SaveMacro, changes: true),
            Tool<DeleteMacroArgs, MacroChangeResult>("delete_macro", "Delete a Macro",
                "Deletes a macro of a virtual controller.",
                AutomationLevel.ChangeSettings, false, true, true, DeleteMacro, changes: true),
            Tool<NoArguments, UndoResult>("undo", "Undo Your Changes",
                "Puts back what PadForge was before this connection's latest run of changes, as one step, and says what came back. It undoes only the newest run of changes, and only while no other program changed things since.",
                AutomationLevel.ChangeSettings, false, true, false, Undo),
            Tool<CheckRequestArgs, AssistantRequestState>("check_request", "Check a Request",
                "Says how a change that waited for the user's answer ended, by the request ID it returned.",
                AutomationLevel.ChangeSettings, true, false, true, (inv, a, _) => Task.FromResult(inv.Calls.Check(inv.Caller, a.RequestId))),
        };

        /// <summary>The most tools the list holds (#600), a budget a test
        /// pins.</summary>
        internal const int MostTools = 39;

        /// <summary>The most bytes the whole tools/list may take (#600), a
        /// budget a test pins, since a host reads every tool's schema into
        /// its model's context.</summary>
        internal const int MostListBytes = 90000;

        private static string Number(int controller) => controller.ToString(CultureInfo.InvariantCulture);

        // ─────────────────────────────────────────────
        //  Commands, through the core's executor
        // ─────────────────────────────────────────────

        /// <summary>Runs a line of the command set and gives what followed
        /// its ok, or refuses with the error word's meaning.</summary>
        private static string Command(AssistantInvocation inv, string line)
        {
            var answer = inv.Host.Commands.Answer(line, inv.Stopping, inv.Caller.Program);
            try { answer.AfterWritten?.Invoke(); } catch { }
            string text = answer.Line ?? AutomationCommands.Internal;
            if (text == AutomationCommands.Ok) return string.Empty;
            if (text.StartsWith(AutomationCommands.Ok + " ", StringComparison.Ordinal)) return text.Substring(3);
            string word = text.StartsWith("error ", StringComparison.Ordinal) ? text.Substring(6).Trim() : "internal";
            throw word switch
            {
                "unknown-profile" => new AssistantRefusal(word, "No profile has that name or ID. list_profiles gives them."),
                "unknown-slot" => new AssistantRefusal(word, "No virtual controller has that number. list_controllers gives them."),
                "busy" => new AssistantRefusal(word, "PadForge is still stopping the engine. Wait a few seconds and call again."),
                "not-available" => new AssistantRefusal(word, "PadForge cannot do this now. status says why."),
                "not-allowed" => new AssistantRefusal(word, "The Automation level does not allow this. Only the user can change it."),
                // A profile switch that would start an extra that listens on
                // the network (#600): the card asks, and the switch is made
                // when the user allows it.
                "waiting" => new AssistantRefusal(word, "PadForge asks the user on its question card first, since the profile turns on an extra that listens on the network. It switches when the user allows it. Tell the user to look at the card, and do not ask again meanwhile. status says which profile is in force."),
                "too-many-questions" => new AssistantRefusal(word, "Too many questions already wait for the user. Nothing changed. Wait for them to be answered, then ask again."),
                // A program's snapshot against the command set's limits
                // (AutomationCommands.Limit): one a minute, and none while
                // the most PadForge takes from programs wait for review.
                "too-soon" => new AssistantRefusal(word, "A program may have one diagnostics snapshot saved a minute. Wait a minute, then call again."),
                "limit-reached" => new AssistantRefusal(word, AutomationCommands.MostWaitingFromPrograms
                    + " snapshots that programs took already wait for the user's review on Home, the most PadForge takes from programs. Ask the user to save or delete them there, then call again."),
                "bad-argument" => new AssistantRefusal(word, "PadForge did not take what this call gave it."),
                _ => new AssistantRefusal("internal", "PadForge did not do it. The message line in its window says why."),
            };
        }

        private static Task<StatusResult> Status(AssistantInvocation inv, NoArguments _, CancellationToken ct)
        {
            string json = Command(inv, "state");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var result = new StatusResult
            {
                PadforgeVersion = root.TryGetProperty("version", out var v) ? v.GetString() : null,
                Engine = root.TryGetProperty("engine", out var e) ? e.GetString() : null,
                Window = root.TryGetProperty("window", out var w) ? w.GetString() : null,
                Recording = root.TryGetProperty("recording", out var r) && r.ValueKind == JsonValueKind.True,
            };
            if (root.TryGetProperty("profile", out var p))
            {
                if (p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) result.Profile = OutsideText.Of(n.GetString());
                if (p.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) result.ProfileId = id.GetString();
                result.ProfileHeld = p.TryGetProperty("pinned", out var pinned) && pinned.ValueKind == JsonValueKind.True;
            }
            if (root.TryGetProperty("controllers", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in list.EnumerateArray())
                {
                    result.Controllers.Add(new StatusController
                    {
                        Number = c.GetProperty("slot").GetInt32(),
                        Name = c.TryGetProperty("name", out var cn) ? cn.GetString() : null,
                        Type = c.TryGetProperty("type", out var ct2) ? AssistantNames.FromPipe(ct2.GetString()) : null,
                        On = c.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True,
                        Devices = c.TryGetProperty("devices", out var d) ? d.GetInt32() : 0,
                        Online = c.TryGetProperty("online", out var o) ? o.GetInt32() : 0,
                    });
                }
            }
            if (result.Engine == "stopped") result.Message = "The engine is stopped, so games see no virtual controller. set_engine starts it.";
            // This connection's own level, the user's cap and what it asked
            // for (#600).
            var level = inv.Level;
            result.AutomationLevel = AutomationLevels.Word(level).Replace('-', '_');
            result.LevelAllows = AssistantGuidance.LevelAllows(level);
            if (inv.Caller.Session != null && inv.Host.Sessions.ConsentWaits(inv.Caller.Session))
                result.Message = "PadForge asks the user on its question card whether this program may work at the level it asked for. Until they answer it works at the Commands level.";
            if (inv.Allows(AutomationLevel.Read)) result.System = inv.OnUi(inv.App.ReadSystem);
            result.HigherLevels = HigherLevels(level);
            result.HeldBack = HeldBackWords();
            if (root.TryGetProperty("stops_behind", out var behind) && behind.ValueKind == JsonValueKind.True)
                result.EngineBehind = EngineBehindWords;
            return Task.FromResult(result);
        }

        /// <summary>What an assistant is told once, when its call puts the
        /// gate in force (#600).</summary>
        internal const string GateNotice = "While you drive PadForge, the key and mouse output you feed is held back from any window that runs with administrator rights. The user's own keys and mouse still go out.";

        internal const string EngineBehindWords = "Continue Polling When Window Loses Focus is off, so the engine stops while PadForge is not the program in front.";

        /// <summary>What status says while the gate holds: while a program
        /// drives PadForge, or output still carries a program's mark, naming
        /// the program of the window in front when output was held from it
        /// (#600). Null otherwise.</summary>
        internal static string HeldBackWords()
        {
            if (!PadForge.Common.Input.ProgramInputGate.InForce && !PadForge.Common.Input.ProgramInputGate.MarksKept) return null;
            string name = PadForge.Common.Input.ProgramInputGate.ProgramNameOf(
                PadForge.Common.Input.ProgramInputGate.HeldFromWindowInFront());
            return name == null ? GateNotice
                : GateNotice + " Output was held from " + name + ", which runs as administrator and is in front now.";
        }

        /// <summary>A call's words, with the gate's notice after them when
        /// the gate was not in force before it (#600).</summary>
        private static string WithGateNotice(string message, bool wasInForce)
            => wasInForce ? message : message + " " + GateNotice;

        /// <summary>The tools each level above <paramref name="level"/> would
        /// add, with their arguments (#600).</summary>
        internal static List<AssistantLevelTools> HigherLevels(AutomationLevel level)
        {
            var list = new List<AssistantLevelTools>();
            foreach (var above in new[] { AutomationLevel.Read, AutomationLevel.SendInputs, AutomationLevel.ChangeSettings })
            {
                if (above <= level) continue;
                var tools = new AssistantLevelTools { Level = AutomationLevels.Word(above).Replace('-', '_') };
                foreach (var tool in All.Where(t => t.Level == above))
                    tools.Tools.Add(new AssistantToolSummary { Name = tool.Name, Title = tool.Title, Arguments = AssistantJson.ArgumentNames(tool.Arguments) });
                list.Add(tools);
            }
            return list;
        }

        private static Task<SwitchProfileResult> SwitchProfile(AssistantInvocation inv, SwitchProfileArgs a, CancellationToken ct)
        {
            inv.Detail = a.Profile;
            if (string.IsNullOrWhiteSpace(a.Profile))
            {
                Command(inv, "deactivate");
                return Task.FromResult(new SwitchProfileResult { Profile = null, Held = false });
            }
            if (a.Profile.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new AssistantRefusal("bad-argument", "A profile's name is one line.");
            string name = Command(inv, "activate " + a.Profile.Trim());
            return Task.FromResult(new SwitchProfileResult { Profile = OutsideText.Of(name), Held = true });
        }

        private static Task<DoneResult> SetEngine(AssistantInvocation inv, OnOffArgs a, CancellationToken ct)
        {
            Command(inv, a.On ? "engine on" : "engine off");
            return Task.FromResult(new DoneResult
            {
                Message = a.On ? "The engine runs, or is starting." : "The engine is stopping. Games lose their virtual controllers.",
            });
        }

        private static Task<DoneResult> SetControllersPower(AssistantInvocation inv, ControllersPowerArgs a, CancellationToken ct)
        {
            string onOff = a.On ? "on" : "off";
            if (a.Controller is int number)
            {
                inv.Detail = Number(number);
                if (number < 1 || number > InputManager.MaxPads)
                    throw new AssistantRefusal("unknown-slot", "A controller's number is 1 to 16. list_controllers gives them.");
                Command(inv, "slot " + Number(number) + " " + onOff);
                return Task.FromResult(new DoneResult { Message = "Controller " + number + " is " + onOff + "." });
            }
            Command(inv, "controllers " + onOff);
            return Task.FromResult(new DoneResult { Message = "Every virtual controller is " + onOff + "." });
        }


        // ─────────────────────────────────────────────
        //  Read
        // ─────────────────────────────────────────────

        private static async Task<ControllersResult> ListControllers(AssistantInvocation inv, ListControllersArgs a, CancellationToken ct)
        {
            var result = inv.OnUi(() =>
            {
                var r = new ControllersResult { Controllers = inv.App.ListControllers(), Revision = inv.App.Revision };
                if (a.Presets)
                {
                    r.Presets = new Dictionary<string, List<AssistantPreset>>();
                    foreach (string type in AssistantNames.ControllerTypes) r.Presets[type] = inv.App.Presets(type);
                }
                return r;
            });
            if (a.Outputs)
            {
                foreach (var controller in result.Controllers)
                {
                    var outputs = inv.App.Outputs(controller.Number);
                    if (outputs != null && outputs.TryGetValue("type", out string word)) outputs["type"] = AssistantNames.FromPipe(word);
                    controller.Outputs = outputs;
                }
            }
            return await Task.FromResult(result).ConfigureAwait(false);
        }

        /// <summary>The devices as Step 1 lists them, read under the list's
        /// lock, and the program devices that have no row yet. Off the
        /// interface's thread, as the scripts' devices line reads them.</summary>
        private static DevicesResult ListDevices(AssistantInvocation inv, ListDevicesArgs a)
        {
            inv.Detail = a.Kind ?? a.Words;
            var result = new DevicesResult { Revision = inv.App.Revision };
            var devices = SettingsManager.UserDevices;
            var seen = new HashSet<Guid>();
            if (devices != null)
            {
                List<UserDevice> rows;
                lock (devices.SyncRoot) rows = devices.Items.Where(d => d != null).ToList();
                foreach (var ud in rows)
                {
                    if (!seen.Add(ud.InstanceGuid)) continue;
                    var summary = Summarize(ud);
                    if (a.Kind != null && !string.Equals(summary.Kind, a.Kind.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    if (a.Assigned is bool assigned && (summary.Controllers.Count > 0) != assigned) continue;
                    if (a.Online is bool online && summary.Online != online) continue;
                    if (!string.IsNullOrWhiteSpace(a.Words) && !Matches(summary.Name?.Text + " " + summary.Maker?.Text, a.Words)) continue;
                    result.Devices.Add(summary);
                }
            }
            // What plugins declare for each device (#600), read where the
            // providers are read.
            if (result.Devices.Count > 0)
            {
                var declared = inv.OnUi(() => PadForge.Services.Catalog.LiveCatalog.Items()
                    .Where(i => i.Id.StartsWith("device/", StringComparison.Ordinal)).Select(i => i.Id).ToList());
                foreach (var device in result.Devices)
                {
                    string prefix = "device/" + device.Id + "/";
                    var mine = declared.Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                    if (mine.Count > 0) device.Settings = mine;
                }
            }
            return result;
        }

        private static bool Matches(string name, string words)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string word in words.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (name.IndexOf(word, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
            return true;
        }

        /// <summary>The kind word of an assistant's lists: the pipe's word
        /// with its hyphens as underscores.</summary>
        internal static string KindOf(UserDevice ud) => AutomationDevices.TypeWord(ud.CapType).Replace('-', '_');

        /// <summary>The name a device is shown by to an assistant: the name
        /// its product gives and nothing else. A name the user gave it, its
        /// Bluetooth name and its serial number never leave, so a device
        /// whose product gives no name is named by its kind.</summary>
        internal static string DeviceName(UserDevice ud)
        {
            if (!string.IsNullOrWhiteSpace(ud.ProductName)) return ud.ProductName.Trim();
            string kind = KindOf(ud).Replace('_', ' ');
            return char.ToUpperInvariant(kind[0]) + kind.Substring(1);
        }

        private static DeviceSummary Summarize(UserDevice ud)
        {
            int sliders = 0;
            if (ud.DeviceObjects != null) foreach (var o in ud.DeviceObjects) if (o.IsSlider) sliders++;
            return new DeviceSummary
            {
                Id = AutomationDevices.IdOf(ud.InstanceGuid),
                Name = OutsideText.Of(AssistantTools.DeviceName(ud)),
                Kind = KindOf(ud),
                Online = ud.IsOnline,
                Buttons = ud.CapButtonCount,
                Axes = ud.CapAxeCount,
                Sliders = sliders,
                Hats = ud.CapPovCount,
                Gyro = ud.HasGyro,
                Touchpad = ud.HasTouchpad,
                Rumble = ud.HasForceFeedback,
                Controllers = ControllersOf(ud.InstanceGuid),
                Hidden = ud.HidHideEnabled,
                FromProgram = ud.CapType == InputDeviceType.Program,
                Maker = OutsideText.Of(AssistantDeviceMaker.Of(ud)),
            };
        }

        /// <summary>The numbers of the controllers a device is assigned to,
        /// as the sidebar numbers them.</summary>
        internal static List<int> ControllersOf(Guid device)
        {
            var list = new List<int>();
            foreach (int slot in SettingsManager.GetAssignedSlots(device))
            {
                int number = SettingsManager.SlotOrders.GetGlobalSlotNumber(slot);
                list.Add(number > 0 ? number : slot + 1);
            }
            list.Sort();
            return list;
        }

        /// <summary>A device by its short ID, refused when none has it.</summary>
        internal static UserDevice DeviceBy(AssistantInvocation inv, string id)
        {
            var (ud, entry) = inv.Host.Sessions.FindDevice(id?.Trim());
            if (ud != null) return ud;
            if (entry != null)
            {
                var stand = new UserDevice();
                stand.LoadFromExternalDevice(entry.Device);
                stand.IsOnline = false;
                return stand;
            }
            throw new AssistantRefusal("unknown-device", "No device has the ID " + id + ". list_devices gives them.");
        }

        private static DeviceDetail GetDevice(AssistantInvocation inv, DeviceArgs a)
        {
            inv.Detail = a.Device;
            var ud = DeviceBy(inv, a.Device);
            var detail = new DeviceDetail
            {
                Id = AutomationDevices.IdOf(ud.InstanceGuid),
                Name = OutsideText.Of(AssistantTools.DeviceName(ud)),
                Kind = KindOf(ud),
                Online = ud.IsOnline,
                Controllers = ControllersOf(ud.InstanceGuid),
            };
            // The rows the scripts' caps line writes, read back: one
            // implementation of what a device has.
            List<string> rows;
            var list = SettingsManager.UserDevices;
            if (list != null) lock (list.SyncRoot) rows = AutomationDevices.Caps(ud);
            else rows = AutomationDevices.Caps(ud);
            foreach (string row in rows)
            {
                var r = AutomationLine.Parse(row);
                if (r.Error != null) continue;
                switch (r.Verb)
                {
                    case "axis":
                    {
                        int index = int.Parse(r.Get("index"), CultureInfo.InvariantCulture);
                        bool slider = index >= ProgramAxis.FirstSlider;
                        int own = slider ? index - ProgramAxis.FirstSlider : index;
                        detail.Controls.Add(new DeviceControl
                        {
                            Kind = slider ? "slider" : "axis",
                            Index = own,
                            Name = OutsideText.Of(r.Get("name")),
                            Rest = int.TryParse(r.Get("rest"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rest)
                                ? Plain(PlainUnits.AxisToPlain(rest, rest)) : null,
                            Range = PlainUnits.IsCentered(int.TryParse(r.Get("rest"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int known)
                                    ? known : PlainUnits.DefaultRest(index))
                                ? "stick" : "trigger",
                            Input = (slider ? "Slider " : "Axis ") + own.ToString(CultureInfo.InvariantCulture),
                        });
                        break;
                    }
                    case "button":
                    {
                        int index = int.Parse(r.Get("index"), CultureInfo.InvariantCulture);
                        detail.Controls.Add(new DeviceControl
                        {
                            Kind = "button", Index = index, Name = OutsideText.Of(r.Get("name")),
                            Input = "Button " + index.ToString(CultureInfo.InvariantCulture),
                        });
                        break;
                    }
                    case "hat":
                    {
                        int index = int.Parse(r.Get("index"), CultureInfo.InvariantCulture);
                        detail.Controls.Add(new DeviceControl
                        {
                            Kind = "hat", Index = index, Name = OutsideText.Of(r.Get("name")),
                            Input = "POV " + index.ToString(CultureInfo.InvariantCulture) + " Up",
                        });
                        break;
                    }
                    case "gyro": detail.Features.Add("gyro"); break;
                    case "accel": detail.Features.Add("accel"); break;
                    case "battery": detail.Features.Add("battery"); break;
                    case "touchpad": detail.Features.Add("touchpad " + r.Get("index") + " with " + r.Get("fingers") + " fingers"); break;
                    case "feedback":
                        if (r.Get("rumble") == "1") detail.Feedback.Add("rumble");
                        if (r.Get("trigrumble") == "1") detail.Feedback.Add("trigger rumble");
                        if (r.Get("light") == "1") detail.Feedback.Add("light");
                        if (r.Get("player") == "1") detail.Feedback.Add("player number");
                        if (r.Get("trigfx") == "1") detail.Feedback.Add("trigger effects");
                        break;
                }
            }
            if (a.Live && ud.IsOnline && AutomationDevices.CopyState(ud) is { } state)
                detail.State = StateOf(state, ButtonsOf(ud), AutomationSessions.RestsOf(ud, null));
            return detail;
        }

        /// <summary>How many buttons a device's state holds, as the pipe's
        /// read of it counts them (AutomationSessions.Read): its count, or
        /// past its last button's index.</summary>
        internal static int ButtonsOf(UserDevice ud)
            => Math.Max(ud.CapButtonCount, ud.CapButtonIndices is { Length: > 0 } b ? b[^1] + 1 : 0);

        /// <summary>A device's state in plain units (#600), each axis read
        /// against its rest.</summary>
        internal static DeviceState StateOf(CustomInputState s, int buttons, int[] rests)
        {
            var state = new DeviceState { Battery = s.BatteryPercent, Charging = s.BatteryCharging };
            for (int i = 0; i < Math.Min(buttons, s.Buttons.Length); i++) if (s.Buttons[i]) state.ButtonsDown.Add(i);
            for (int i = 0; i < s.Axis.Length; i++) state.Axes.Add(Plain(PlainUnits.AxisToPlain(s.Axis[i], rests[i])));
            for (int i = 0; i < s.Sliders.Length; i++)
                state.Sliders.Add(Plain(PlainUnits.AxisToPlain(s.Sliders[i], rests[ProgramAxis.FirstSlider + i])));
            foreach (int hat in s.Povs)
            {
                state.Hats.Add(PlainUnits.HatToPlain(hat));
                state.HatDirections.Add(PlainUnits.DirectionsText(PlainUnits.HatToDirections(hat)));
            }
            return state;
        }

        /// <summary>A plain value at the precision a program reads it.</summary>
        internal static double Plain(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero) + 0.0;

        /// <summary>The waiting step of wait_for_input: how often the
        /// devices are compared, and how far an axis moves before it
        /// counts.</summary>
        internal const int WaitStepMs = 10, AxisMoveCounts = 16384;

        private static async Task<InputResult> WaitForInput(AssistantInvocation inv, WaitForInputArgs a, CancellationToken ct)
        {
            int seconds = Math.Clamp(a.Seconds ?? 15, 1, 60);
            inv.Detail = a.Device;
            UserDevice only = a.Device == null ? null : DeviceBy(inv, a.Device);
            if (a.Timeline) return await TimelineAsync(only, seconds, ct).ConfigureAwait(false);
            var start = new Dictionary<Guid, CustomInputState>();
            List<UserDevice> Watched()
            {
                if (only != null) return new List<UserDevice> { only };
                var list = SettingsManager.UserDevices;
                if (list == null) return new List<UserDevice>();
                lock (list.SyncRoot) return list.Items.Where(d => d != null && d.IsOnline).ToList();
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                foreach (var ud in Watched())
                {
                    var now = AutomationDevices.CopyState(ud);
                    if (now == null) continue;
                    if (!start.TryGetValue(ud.InstanceGuid, out var before))
                    {
                        // What a device read when it was first seen is its
                        // start: a button already held is not a press.
                        start[ud.InstanceGuid] = now;
                        continue;
                    }
                    var found = FirstChange(before, now);
                    if (found != null) return Found(ud, found.Value.Kind, found.Value.Index, found.Value.Value);
                }
                await Task.Delay(WaitStepMs, ct).ConfigureAwait(false);
            }
            return new InputResult { Found = false, Message = "Nothing moved in " + seconds + " seconds. Ask the user to press the control again, then call this again." };
        }

        /// <summary>The most changes a timeline holds. The rest are
        /// counted.</summary>
        internal const int MostTimeline = 2000;

        /// <summary>
        /// wait_for_input with timeline (#600): every change of every
        /// watched device for the whole time, with its milliseconds, read on
        /// a thread of its own once a millisecond and each time a device
        /// publishes a new state (its InputStateSeq), so a change is read
        /// in the pass it was made. Engine passes that went by between two
        /// reads are counted, since a change made and undone inside them is
        /// not seen. An axis counts a change of one unit in plain units.
        /// </summary>
        private static async Task<InputResult> TimelineAsync(UserDevice only, int seconds, CancellationToken ct)
        {
            var result = new InputResult { Timeline = new List<InputChange>() };
            await Task.Factory.StartNew(() =>
            {
                var last = new Dictionary<Guid, (CustomInputState State, long Seq)>();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(seconds) && !ct.IsCancellationRequested)
                {
                    List<UserDevice> watched;
                    if (only != null) watched = new List<UserDevice> { only };
                    else
                    {
                        var list = SettingsManager.UserDevices;
                        if (list == null) watched = new List<UserDevice>();
                        else lock (list.SyncRoot) watched = list.Items.Where(d => d != null && d.IsOnline).ToList();
                    }
                    foreach (var ud in watched)
                    {
                        long seq = System.Threading.Volatile.Read(ref ud.InputStateSeq);
                        if (last.TryGetValue(ud.InstanceGuid, out var seen) && seen.Seq == seq && ud.Device is not ProgramDevice) continue;
                        var now = AutomationDevices.CopyState(ud);
                        if (now == null) continue;
                        if (!last.TryGetValue(ud.InstanceGuid, out var before))
                        {
                            last[ud.InstanceGuid] = (now, seq);
                            continue;
                        }
                        if (seq > before.Seq + 1) result.PassesUnread += seq - before.Seq - 1;
                        last[ud.InstanceGuid] = (before.State, seq);
                        foreach (var (input, value) in Changes(ud, before.State, now))
                        {
                            if (result.Timeline.Count >= MostTimeline) { result.Dropped++; continue; }
                            result.Timeline.Add(new InputChange { Ms = clock.ElapsedMilliseconds, Device = AutomationDevices.IdOf(ud.InstanceGuid), Input = input, Value = value });
                        }
                    }
                    Thread.Sleep(1);
                }
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            result.Found = result.Timeline.Count > 0;
            result.Message = result.Found
                ? result.Timeline.Count + " changes in " + seconds + " seconds."
                : "Nothing moved in " + seconds + " seconds.";
            return result;
        }

        /// <summary>Every input that changed since it was last reported, in
        /// plain units: a button pressed or let go, a hat that turned, an
        /// axis or slider that moved one unit or more. What is reported is
        /// written into <paramref name="reported"/>, so a slow axis is
        /// reported once it has moved a unit in all.</summary>
        internal static List<(string Input, double Value)> Changes(UserDevice ud, CustomInputState reported, CustomInputState now)
        {
            var changes = new List<(string, double)>();
            int[] rests = AutomationSessions.RestsOf(ud, null);
            for (int i = 0; i < now.Buttons.Length && i < reported.Buttons.Length; i++)
            {
                if (now.Buttons[i] == reported.Buttons[i]) continue;
                reported.Buttons[i] = now.Buttons[i];
                changes.Add(("Button " + i.ToString(CultureInfo.InvariantCulture), now.Buttons[i] ? 1 : 0));
            }
            for (int i = 0; i < now.Povs.Length && i < reported.Povs.Length; i++)
            {
                if (now.Povs[i] == reported.Povs[i]) continue;
                reported.Povs[i] = now.Povs[i];
                changes.Add((now.Povs[i] < 0 ? "POV " + i.ToString(CultureInfo.InvariantCulture) : "POV " + i.ToString(CultureInfo.InvariantCulture) + " " + HatWord(now.Povs[i]),
                    PlainUnits.HatToPlain(now.Povs[i])));
            }
            for (int i = 0; i < now.Axis.Length && i < reported.Axis.Length; i++)
            {
                double a = PlainUnits.AxisToPlain(reported.Axis[i], rests[i]), b = PlainUnits.AxisToPlain(now.Axis[i], rests[i]);
                if (Math.Abs(b - a) < 1) continue;
                reported.Axis[i] = now.Axis[i];
                changes.Add(("Axis " + i.ToString(CultureInfo.InvariantCulture), Plain(b)));
            }
            for (int i = 0; i < now.Sliders.Length && i < reported.Sliders.Length; i++)
            {
                int r = rests[ProgramAxis.FirstSlider + i];
                double a = PlainUnits.AxisToPlain(reported.Sliders[i], r), b = PlainUnits.AxisToPlain(now.Sliders[i], r);
                if (Math.Abs(b - a) < 1) continue;
                reported.Sliders[i] = now.Sliders[i];
                changes.Add(("Slider " + i.ToString(CultureInfo.InvariantCulture), Plain(b)));
            }
            return changes;
        }

        private static (string Kind, int Index, int Value)? FirstChange(CustomInputState before, CustomInputState now)
        {
            for (int i = 0; i < now.Buttons.Length && i < before.Buttons.Length; i++)
                if (now.Buttons[i] && !before.Buttons[i]) return ("button", i, 1);
            for (int i = 0; i < now.Povs.Length && i < before.Povs.Length; i++)
                if (now.Povs[i] != before.Povs[i] && now.Povs[i] >= 0) return ("hat", i, now.Povs[i]);
            for (int i = 0; i < now.Axis.Length && i < before.Axis.Length; i++)
                if (Math.Abs(now.Axis[i] - before.Axis[i]) >= AxisMoveCounts) return ("axis", i, now.Axis[i]);
            for (int i = 0; i < now.Sliders.Length && i < before.Sliders.Length; i++)
                if (Math.Abs(now.Sliders[i] - before.Sliders[i]) >= AxisMoveCounts) return ("slider", i, now.Sliders[i]);
            return null;
        }

        private static InputResult Found(UserDevice ud, string kind, int index, int value)
        {
            string name = null;
            if (ud.DeviceObjects != null)
            {
                foreach (var o in ud.DeviceObjects)
                {
                    bool match = kind switch
                    {
                        "button" => o.IsButton && o.InputIndex == index,
                        "hat" => o.IsPov && o.InputIndex == index,
                        "slider" => o.IsSlider && o.InputIndex == index,
                        _ => o.IsAxis && !o.IsSlider && o.InputIndex == index,
                    };
                    if (match) { name = o.Name; break; }
                }
            }
            string input = kind switch
            {
                "button" => "Button " + index,
                "hat" => "POV " + index + " " + HatWord(value),
                "slider" => "Slider " + index,
                _ => "Axis " + index,
            };
            return new InputResult
            {
                Found = true,
                Device = AutomationDevices.IdOf(ud.InstanceGuid),
                DeviceName = OutsideText.Of(AssistantTools.DeviceName(ud)),
                Kind = kind,
                Index = index,
                Name = OutsideText.Of(name),
                Input = input,
                Value = kind switch
                {
                    "button" => 1,
                    "hat" => PlainUnits.HatToPlain(value),
                    "slider" => Plain(PlainUnits.AxisToPlain(value, RestOfAxis(ud, ProgramAxis.FirstSlider + index))),
                    _ => Plain(PlainUnits.AxisToPlain(value, RestOfAxis(ud, index))),
                },
            };
        }

        /// <summary>An axis's rest on a device, or the default rest where
        /// PadForge names none.</summary>
        private static int RestOfAxis(UserDevice ud, int index) => AutomationSessions.RestsOf(ud, null)[index];

        /// <summary>The direction a hat's angle is nearest, as a mapping row
        /// names it.</summary>
        internal static string HatWord(int centidegrees)
        {
            int at = ((centidegrees % 36000) + 36000) % 36000;
            if (at >= 31500 || at < 4500) return "Up";
            if (at < 13500) return "Right";
            if (at < 22500) return "Down";
            return "Left";
        }

        private static Task<RecordResult> RecordProblem(AssistantInvocation inv, RecordArgs a, CancellationToken ct)
            => RecordAsync(inv, a, ct);

        /// <summary>record_problem (#600): a recording at Read, or with
        /// seconds 0 a snapshot, which Commands allows as the command set
        /// does. A program is told the file waits for the user, never
        /// where.</summary>
        private static async Task<RecordResult> RecordAsync(AssistantInvocation inv, RecordArgs a, CancellationToken ct)
        {
            inv.Detail = a.Note;
            if (a.Seconds == 0)
            {
                Command(inv, "snapshot");
                return new RecordResult { Message = "The snapshot waits in PadForge for the user to look over and save for a bug report." };
            }
            inv.Requires(AutomationLevel.Read, "a recording");
            int seconds = Math.Clamp(a.Seconds ?? 10, 3, 60);
            var recording = await inv.App.RecordAsync(seconds, a.Note, ct).ConfigureAwait(false);
            return new RecordResult
            {
                Summary = recording.Summary,
                Message = "The recording waits in PadForge for the user to look over and save for a bug report.",
            };
        }

        private static Task<ProfilesResult> ListProfiles(AssistantInvocation inv, ListProfilesArgs a, CancellationToken ct)
        {
            inv.Detail = a.Profile;
            return Task.FromResult(inv.OnUi(() => new ProfilesResult
            {
                Profiles = inv.App.ListProfiles(),
                Profile = string.IsNullOrWhiteSpace(a.Profile) ? null : inv.App.ReadProfile(a.Profile),
            }));
        }

        private static Task<MappingsResult> GetMappings(AssistantInvocation inv, GetMappingsArgs a, CancellationToken ct)
        {
            inv.Detail = Number(a.Controller);
            return Task.FromResult(inv.OnUi(() => new MappingsResult
            {
                Controller = a.Controller,
                Rows = inv.App.ReadMappings(a.Controller),
                Macros = a.Macros ? inv.App.ReadMacros(a.Controller) : null,
                Menus = a.Macros ? inv.App.ReadMenus(a.Controller) : null,
            }));
        }

        /// <summary>The default seconds a step stays when it goes on after
        /// some.</summary>
        internal const int DefaultStepSeconds = 5;

        /// <summary>
        /// show (#600): the guided pointer. One place or several steps, each
        /// in the program's words, or none to take the callout away. While
        /// PadForge's window is not in front the question card asks the
        /// user first, which program wants to show what, with Not Now and
        /// Show, and the steps play once the user picks Show. A program never
        /// brings the window forward. The user's click on Show does.
        /// </summary>
        private static async Task<AssistantShown> Show(AssistantInvocation inv, ShowArgs a, CancellationToken ct)
        {
            var steps = new List<AssistantShowStep>();
            if (a.Steps != null && a.Steps.Count > 0)
            {
                if (a.Place != null) throw new AssistantRefusal("bad-argument", "show takes place or steps, not both.");
                foreach (var s in a.Steps) steps.Add(StepOf(s.Place, s.Words, s.Advance, s.Seconds));
            }
            else if (!string.IsNullOrWhiteSpace(a.Place))
                steps.Add(StepOf(a.Place, a.Words, a.Advance, a.Seconds));
            if (steps.Count > 20) throw new AssistantRefusal("bad-argument", "show takes at most 20 steps.");
            string program = inv.Caller.Program?.Name ?? "Assistant";
            var request = new AssistantShowRequest { Program = program, Steps = steps, Walk = a.Walk, Owner = inv.Caller.Key };
            inv.Detail = steps.Count == 0 ? null : steps[^1].Place;
            if (steps.Count == 0) return inv.OnUi(() => inv.Catalog.Show(request));

            var place = inv.OnUi(inv.App.WindowPlace);
            if (place == AssistantWindowPlace.InFront) return inv.OnUi(() => inv.Catalog.Show(request));

            // Not in front: every step is checked as the show checks it, so
            // a place the user cannot be shown never raises the card. Then
            // the question card asks, at its own place.
            var last = inv.OnUi(() => inv.Catalog.CheckShow(request));
            var question = new ApprovalRequest(inv.Caller.Program, AssistantToolWords.AskShow(program, last.Name),
                new[] { AssistantToolWords.ShowWhere(last.Where) }) { Kind = ApprovalKind.Show, From = inv.Caller.Session?.AskerLine };
            var answer = inv.Host.Approvals.AskAsync(question, inv.Caller.Session?.Lifetime ?? inv.Stopping);
            if (answer.IsCompletedSuccessfully && answer.Result == ApprovalAnswer.TooMany)
                throw new AssistantRefusal("too-many-questions", "Too many questions already wait for the user. Wait, then show again.");
            var ui = inv.Ui;
            var app = inv.App;
            var catalog = inv.Catalog;
            _ = answer.ContinueWith(done =>
            {
                if (!done.IsCompletedSuccessfully || done.Result != ApprovalAnswer.Allowed) return;
                ui.Post(() =>
                {
                    // The user's click on Show brings the window forward.
                    app.BringForward();
                    try { catalog.Show(request); }
                    catch (AssistantRefusal) { /* the place went away meanwhile */ }
                });
            }, TaskScheduler.Default);
            await Task.CompletedTask.ConfigureAwait(false);
            return new AssistantShown
            {
                Shown = false,
                Asked = true,
                Where = last.Where,
                Window = place,
                Steps = steps.Count,
                Message = "PadForge's window is not in front, so its question card asks the user whether to show it. It plays once the user picks Show. Tell the user to look at the card.",
            };
        }

        private static AssistantShowStep StepOf(string place, string words, AssistantAdvance? advance, int? seconds)
        {
            if (string.IsNullOrWhiteSpace(place)) throw new AssistantRefusal("bad-argument", "Each step names a place by its ID, from find.");
            return new AssistantShowStep
            {
                Place = place.Trim(),
                Words = words,
                Advance = advance ?? AssistantAdvance.Click,
                Seconds = Math.Clamp(seconds ?? DefaultStepSeconds, 1, 60),
            };
        }

        // ─────────────────────────────────────────────
        //  Send Inputs, through the scripts' line verbs
        // ─────────────────────────────────────────────

        /// <summary>Runs a line of the session's own verbs and gives its
        /// answer, or refuses with the verb's code and words.</summary>
        private static async Task<string> Line(AssistantInvocation inv, string body)
        {
            var session = inv.Caller.Session
                ?? throw new AssistantRefusal("not-available", "This connection has no session for devices of its own.");
            var lines = await inv.Host.Sessions.RunCapturedAsync(session, body, inv.Stopping).ConfigureAwait(false);
            string last = lines.Count > 0 ? lines[^1] : "ok";
            if (last.StartsWith("ok", StringComparison.Ordinal)) return last.Length > 3 ? last.Substring(3) : string.Empty;
            // err <code> <words>
            string rest = last.StartsWith("err ", StringComparison.Ordinal) ? last.Substring(4) : last;
            int space = rest.IndexOf(' ');
            string code = space < 0 ? rest : rest.Substring(0, space);
            string words = space < 0 ? AutomationLine.WordsFor(code) : rest.Substring(space + 1);
            throw new AssistantRefusal(code, Sentence(words) + Hint(code));
        }

        private static string Sentence(string words)
            => string.IsNullOrEmpty(words) ? string.Empty : char.ToUpperInvariant(words[0]) + words.Substring(1).TrimEnd('.') + ".";

        private static string Hint(string code) => code switch
        {
            "unknown-device" => " list_devices gives the devices.",
            "not-owner" => " Only the connection that added a device drives it.",
            "offline" => " The device is not connected.",
            "too-many" => string.Empty,
            _ => string.Empty,
        };

        private static async Task<AddDeviceResult> AddDevice(AssistantInvocation inv, AddDeviceArgs a, CancellationToken ct)
        {
            inv.Detail = a.Name ?? a.Key;
            var declaration = new Dictionary<string, object>();
            if (a.Buttons != null) declaration["buttons"] = a.Buttons;
            if (a.Axes != null) declaration["axes"] = a.Axes.Select(x => Axis(x)).ToList();
            if (a.Sliders != null) declaration["sliders"] = a.Sliders.Select(x => Axis(x)).ToList();
            if (a.Hats != null) declaration["hats"] = a.Hats;
            if (a.Gyro) declaration["gyro"] = true;
            if (a.Accel) declaration["accel"] = true;
            if (a.Battery) declaration["battery"] = true;
            if (a.Touchpads != null) declaration["touchpads"] = a.Touchpads;
            if (a.Feedback != null)
                declaration["feedback"] = a.Feedback.Select(f => f switch
                {
                    AssistantFeedback.TriggerRumble => "trigrumble",
                    AssistantFeedback.Light => "light",
                    AssistantFeedback.Player => "player",
                    AssistantFeedback.TriggerEffects => "trigfx",
                    _ => "rumble",
                }).Distinct().ToList();
            string json = JsonSerializer.Serialize(declaration);
            // Words of the line: a key or an ID with a space reaches the
            // verb whole, and is refused there.
            var body = new StringBuilder("declare key=").Append(AutomationLine.Word(a.Key ?? string.Empty));
            if (!string.IsNullOrWhiteSpace(a.Name)) body.Append(" name=").Append(AutomationLine.Quote(a.Name));
            body.Append(' ').Append(json);
            bool wasInForce = PadForge.Common.Input.ProgramInputGate.InForce;
            string answer = await Line(inv, body.ToString()).ConfigureAwait(false);
            string id = answer.StartsWith("id=", StringComparison.Ordinal) ? answer.Substring(3) : answer;
            return new AddDeviceResult
            {
                Device = id,
                Message = WithGateNotice("The device is on the Devices page. Assign it to a controller with assign_device, then drive it with set_device_inputs.", wasInForce),
            };
        }

        private static Dictionary<string, object> Axis(AxisDeclaration axis)
        {
            var d = new Dictionary<string, object>();
            if (axis?.Name != null) d["name"] = axis.Name;
            if (axis?.Rest != null)
            {
                double share = axis.Rest.Value;
                if (double.IsNaN(share) || share < 0 || share > 100)
                    throw new AssistantRefusal("bad-argument", "An axis's rest is 0 to 100, its share of the travel.");
                d["rest"] = PlainUnits.AxisToRaw(share, 0);
            }
            return d;
        }

        private static async Task<DoneResult> SetDeviceInputs(AssistantInvocation inv, SetDeviceInputsArgs a, CancellationToken ct)
        {
            inv.Detail = a.Device;
            // Plain units on the wire (#600): a percent for an axis, which
            // the pipe reads against the axis's rest, and degrees or
            // directions for a hat.
            var body = new StringBuilder("s ").Append(AutomationLine.Word(a.Device));
            if (a.Rest) body.Append(" rest");
            void Add(string prefix, string key, string value)
            {
                if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    throw new AssistantRefusal("bad-argument", "A control's index is a whole number, as in \"3\".");
                body.Append(' ').Append(prefix).Append(index.ToString(CultureInfo.InvariantCulture)).Append('=').Append(value);
            }
            if (a.Buttons != null) foreach (var (k, v) in a.Buttons) Add("b", k, v ? "1" : "0");
            if (a.Axes != null) foreach (var (k, v) in a.Axes) Add("a", k, PlainUnits.Format(v) + "%");
            if (a.Hats != null) foreach (var (k, v) in a.Hats) Add("h", k, v < 0 ? "-1" : PlainUnits.Format(v) + "deg");
            if (a.HatDirections != null)
                foreach (var (k, v) in a.HatDirections)
                {
                    if (string.IsNullOrWhiteSpace(v)) { Add("h", k, "-1"); continue; }
                    if (!PlainUnits.TryParseDirections(v, out var directions))
                        throw new AssistantRefusal("bad-argument", "A hat's directions are Up, Right, Down and Left, two joined by +.");
                    Add("h", k, PlainUnits.DirectionsText(directions));
                }
            bool wasInForce = PadForge.Common.Input.ProgramInputGate.InForce;
            await Line(inv, body.ToString()).ConfigureAwait(false);
            return new DoneResult { Message = WithGateNotice("Set.", wasInForce) };
        }

        private static async Task<DoneResult> RemoveDevice(AssistantInvocation inv, DeviceOnlyArgs a, CancellationToken ct)
        {
            inv.Detail = a.Device;
            await Line(inv, "gone " + AutomationLine.Word(a.Device)).ConfigureAwait(false);
            return new DoneResult { Message = "The device goes offline with its outputs at rest." };
        }

        private static async Task<DoneResult> HoldControl(AssistantInvocation inv, HoldArgs a, CancellationToken ct)
        {
            inv.Detail = a.Device + " " + a.Control;
            string control = (a.Control ?? string.Empty).Trim().ToLowerInvariant();
            if (!AutomationSessions.TryControl(control, out _, out _))
                throw new AssistantRefusal("bad-argument", "A control is b, a or h and its index, as in b3, a0 or h0.");
            if (a.Release)
            {
                await Line(inv, "rel " + AutomationLine.Word(a.Device) + " " + control).ConfigureAwait(false);
                return new DoneResult { Message = "Released. The control reads the device again." };
            }
            if (a.Value == null) throw new AssistantRefusal("bad-argument", "Give the value to hold the control at, or release.");
            bool wasInForce = PadForge.Common.Input.ProgramInputGate.InForce;
            await Line(inv, "own " + AutomationLine.Word(a.Device) + " " + control + "=" + HoldValue(control[0], a.Value.Value)).ConfigureAwait(false);
            return new DoneResult { Message = WithGateNotice("Held until it is released or this connection ends.", wasInForce) };
        }

        /// <summary>A hold's value in plain units, as the pipe's own line
        /// takes it (#600).</summary>
        internal static string HoldValue(char kind, JsonElement value)
        {
            switch (kind)
            {
                case 'b':
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.ValueKind == JsonValueKind.True ? "1" : "0";
                    throw new AssistantRefusal("bad-argument", "A button is held at true or false.");
                case 'a':
                    if (value.ValueKind == JsonValueKind.Number) return PlainUnits.Format(value.GetDouble()) + "%";
                    throw new AssistantRefusal("bad-argument", "An axis is held at a number: -100 to 100 for a stick, 0 to 100 for a trigger or slider.");
                default:
                    if (value.ValueKind == JsonValueKind.Number)
                    {
                        double degrees = value.GetDouble();
                        return degrees < 0 ? "-1" : PlainUnits.Format(degrees) + "deg";
                    }
                    if (value.ValueKind == JsonValueKind.String && PlainUnits.TryParseDirections(value.GetString(), out var directions))
                        return PlainUnits.DirectionsText(directions);
                    throw new AssistantRefusal("bad-argument", "A hat is held at degrees, -1, or directions such as \"Up+Right\".");
            }
        }

        // ─────────────────────────────────────────────
        //  Change Settings, through the calls the pages make
        // ─────────────────────────────────────────────

        private static T Done<T>(T result, string message) where T : AssistantChangeResult
        {
            result.Done = true;
            result.Message ??= message;
            return result;
        }

        /// <summary>set (#600): one place or several, in order. What any of
        /// them asks is asked once, in one question.</summary>
        private static async Task<SetResult> Set(AssistantInvocation inv, SetArgs a, CancellationToken ct)
        {
            var values = new List<(string Id, JsonElement Value)>();
            if (a.Values != null && a.Values.Count > 0)
            {
                if (a.Id != null || a.Value != null) throw new AssistantRefusal("bad-argument", "set takes id and value, or values, not both.");
                foreach (var (id, value) in a.Values) values.Add((id, value.Clone()));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(a.Id) || a.Value == null) throw new AssistantRefusal("bad-argument", "set takes id and value, or values.");
                values.Add((a.Id, a.Value.Value.Clone()));
            }
            if (values.Count > 50) throw new AssistantRefusal("bad-argument", "set takes at most 50 values in one call.");
            inv.Detail = values[0].Id;
            foreach (var (id, _) in values) CheckReach(inv, id);
            var asks = new List<AssistantAsk>();
            inv.OnUi(() =>
            {
                foreach (var (id, value) in values)
                    if (inv.Catalog.AskFor(id, value) is { } ask) asks.Add(ask);
            });
            AssistantAsk question = asks.Count == 0 ? null : asks.Count == 1 ? asks[0]
                : new AssistantAsk(asks[0].Title, asks.SelectMany(q => q.Detail).Distinct().ToList());
            object result = await inv.ChangeAsync(question, () =>
            {
                // What the values would ask, read again as they are set
                // (#600). A set that would by then ask about a line the card
                // did not name, a profile that came to turn on an extra that
                // listens on the network while the card showed, or ask where
                // nothing was asked, is not made, as a press goes only on
                // what its card named (AssistantPressPlan.Check).
                var now = new List<AssistantAsk>();
                foreach (var (id, value) in values)
                    if (inv.Catalog.AskFor(id, value) is { } again) now.Add(again);
                if (!now.SelectMany(again => again.Detail).All(line => question != null && question.Detail.Contains(line)))
                    throw new AssistantRefusal("not-available", "By now the change would do more than PadForge asked about. Set again to ask about what it would do now.");
                var set = new SetResult();
                foreach (var (id, value) in values) set.Places.Add(inv.Catalog.Set(id, value));
                return Done(set, values.Count == 1 ? "Set. The page shows the new value." : "Set, each in turn. The page shows the new values.");
            }).ConfigureAwait(false);
            if (result is SetResult done && done.Places.Count > 0) inv.Detail = done.Places[0].Name;
            return (SetResult)result;
        }

        /// <summary>How long press looks for a dialog its press opened.</summary>
        internal static readonly TimeSpan DialogWait = TimeSpan.FromMilliseconds(1500);

        /// <summary>press (#600): the press is posted to the interface's
        /// thread, so a dialog it opens holds nothing, and a dialog that
        /// opens is read and given back with its text and buttons. A Windows
        /// dialog PadForge cannot read is named as waiting for the
        /// person.</summary>
        private static async Task<PressResult> Press(AssistantInvocation inv, IdArgs a, CancellationToken ct)
        {
            inv.Detail = a.Id;
            CheckReach(inv, a.Id);
            var plan = inv.OnUi(() => inv.Catalog.PlanPress(a.Id));
            inv.Detail = plan.Place.Name;
            var before = inv.OnUi(() => inv.Catalog.Dialogs());
            var ui = inv.Ui;
            object result = await inv.ChangeAsync(plan.Ask, () =>
            {
                plan.Check?.Invoke();
                ui.Post(() =>
                {
                    try { plan.Press(); }
                    catch (Exception error) { PadForge.Engine.SdlDiagLog.WriteLine("Automation: a press failed: " + error.Message); }
                });
                return Done(new PressResult { Place = plan.Place }, "Pressed.");
            }).ConfigureAwait(false);
            if (result is not PressResult pressed || !pressed.Done) return (PressResult)result;

            var known = new HashSet<string>(before.Open.Select(d => d.Id), StringComparer.Ordinal);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < DialogWait)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                var now = inv.OnUi(() => inv.Catalog.Dialogs());
                if (now.WaitingForPerson != null && before.WaitingForPerson == null)
                {
                    pressed.WaitingForPerson = now.WaitingForPerson;
                    pressed.Message = "Pressed. " + now.WaitingForPerson;
                    break;
                }
                var opened = now.Open.FirstOrDefault(d => !known.Contains(d.Id));
                if (opened != null)
                {
                    pressed.Dialog = inv.OnUi(() => inv.Catalog.Read(opened.Id));
                    pressed.Message = "Pressed. It opened a dialog, which waits: read it, and press one of its buttons, or ask the user.";
                    break;
                }
            }
            return pressed;
        }

        private static async Task<DoneResult> SetValue(AssistantInvocation inv, SetValueArgs a, CancellationToken ct)
        {
            inv.Detail = a.Name;
            string name = (a.Name ?? string.Empty).Trim();
            if (!PadForge.Common.Input.ScriptValues.IsName(name))
                throw new AssistantRefusal("bad-argument", "A value's name is a letter, then up to 31 letters, digits, _ . or -.");
            if (a.Release)
            {
                var session = inv.Caller.Session
                    ?? throw new AssistantRefusal("not-available", "This connection has no session that set a value.");
                // Only what this session set last is let go: a value another
                // program set since stays its own.
                bool let = PadForge.Common.Input.ScriptValues.Release(name, session.Number);
                await Task.CompletedTask.ConfigureAwait(false);
                return new DoneResult
                {
                    Message = let || PadForge.Common.Input.ScriptValues.SetterOf(name) == 0
                        ? "Let go. Triggers that read " + name + " read 0."
                        : "Let go. Another program set " + name + " since, so triggers read its value.",
                };
            }
            if (a.Value is not double value || double.IsNaN(value) || double.IsInfinity(value))
                throw new AssistantRefusal("bad-argument", "Give the value as a number, or release.");
            bool wasInForce = PadForge.Common.Input.ProgramInputGate.InForce;
            await Line(inv, "var " + name + "=" + value.ToString("R", CultureInfo.InvariantCulture)).ConfigureAwait(false);
            return new DoneResult { Message = WithGateNotice("Set until it is released or this connection ends.", wasInForce) };
        }

        private static Task<ProfileChangeResult> CreateProfile(AssistantInvocation inv, CreateProfileArgs a, CancellationToken ct)
        {
            inv.Detail = a.Name;
            if (string.IsNullOrWhiteSpace(a.Name)) throw new AssistantRefusal("bad-argument", "A profile needs a name.");
            var result = (ProfileChangeResult)inv.Change(() =>
            {
                string id = inv.App.CreateProfile(a.Name.Trim(), a.CopyOf);
                var made = inv.App.ReadProfile(id);
                return Done(new ProfileChangeResult { ProfileId = id, Name = made.Name }, "Made. switch_profile puts it in force.");
            });
            return Task.FromResult(result);
        }

        private static Task<ProfileChangeResult> RenameProfile(AssistantInvocation inv, RenameProfileArgs a, CancellationToken ct)
        {
            inv.Detail = a.Profile;
            if (string.IsNullOrWhiteSpace(a.Name)) throw new AssistantRefusal("bad-argument", "A profile needs a name.");
            var result = (ProfileChangeResult)inv.Change(() =>
            {
                var before = inv.App.ReadProfile(a.Profile);
                inv.App.RenameProfile(before.Id, a.Name.Trim());
                var after = inv.App.ReadProfile(before.Id);
                return Done(new ProfileChangeResult { ProfileId = after.Id, Name = after.Name }, "Renamed.");
            });
            return Task.FromResult(result);
        }

        private static Task<ProfileChangeResult> DeleteProfile(AssistantInvocation inv, ProfileArgs a, CancellationToken ct)
        {
            inv.Detail = a.Profile;
            var result = (ProfileChangeResult)inv.Change(() =>
            {
                var before = inv.App.ReadProfile(a.Profile);
                inv.App.DeleteProfile(before.Id);
                return Done(new ProfileChangeResult { ProfileId = before.Id, Name = before.Name }, "Deleted. undo puts it back.");
            });
            return Task.FromResult(result);
        }

        /// <summary>Runs file work with the program's own rights, so
        /// PadForge never opens a file for it that it could not open
        /// itself.</summary>
        private static T AsClient<T>(AssistantInvocation inv, Func<T> work)
        {
            var session = inv.Caller.Session;
            T value = default;
            Exception failure = null;
            bool ran = session != null && session.RunAsClient(() =>
            {
                try { value = work(); }
                catch (Exception error) { failure = error; }
            });
            if (!ran)
                throw new AssistantRefusal("not-available", "This connection cannot open files with the program's own rights, so PadForge opens none for it.");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return value;
        }

        /// <summary>The most bytes a profile file may hold.</summary>
        internal const int LongestProfileFile = 16 * 1024 * 1024;

        /// <summary>A profile file's bytes, read once from its open stream
        /// and never past <see cref="LongestProfileFile"/>, whatever length
        /// it gave when it was opened: a program can keep writing to its own
        /// file while PadForge reads it. Throws IOException for a file past
        /// it.</summary>
        internal static byte[] ReadProfileBytes(System.IO.Stream stream)
        {
            using var memory = new System.IO.MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (memory.Length + read > LongestProfileFile) throw new System.IO.IOException("too large");
                memory.Write(chunk, 0, read);
            }
            return memory.ToArray();
        }

        /// <summary>
        /// import_profile. PadForge runs with administrator rights, so it never
        /// opens a file at a program's word with its own: a path is read with
        /// the program's rights (the pipe's client, through RunAsClient), and
        /// the copy an assistant starts, which opens the pipe for
        /// identification only, reads the file itself and sends its bytes.
        /// The import always asks the user, with the programs it would run and
        /// the keys it would press.
        /// </summary>
        private static async Task<ProfileChangeResult> ImportProfile(AssistantInvocation inv, ImportProfileArgs a, CancellationToken ct)
        {
            bool byPath = !string.IsNullOrWhiteSpace(a.Path);
            bool byContent = !string.IsNullOrEmpty(a.ContentBase64);
            if (byPath == byContent)
                throw new AssistantRefusal("bad-argument", "Give the file's full path, or its name and bytes, and not both.");
            string fileName = System.IO.Path.GetFileName(byPath ? a.Path : a.FileName ?? string.Empty);
            inv.Detail = fileName;
            if (string.IsNullOrWhiteSpace(fileName))
                throw new AssistantRefusal("bad-argument", "Give the file's name with its bytes, as in profile.pfprofile.");
            byte[] bytes;
            if (byPath)
            {
                if (!System.IO.Path.IsPathFullyQualified(a.Path))
                    throw new AssistantRefusal("bad-argument", "Give the file's full path, as in C:\\Users\\name\\Downloads\\profile.pfprofile.");
                try
                {
                    bytes = AsClient(inv, () =>
                    {
                        using var file = new System.IO.FileStream(a.Path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
                        return ReadProfileBytes(file);
                    });
                }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    throw new AssistantRefusal("no-file", "PadForge could not read the file with the user's rights (" + error.GetType().Name.Replace("Exception", string.Empty)
                        + "). Check the path, and that the file is under 16 MB.");
                }
            }
            else
            {
                try { bytes = Convert.FromBase64String(a.ContentBase64); }
                catch (FormatException) { throw new AssistantRefusal("bad-argument", "content_base64 is not base64."); }
                if (bytes.Length > LongestProfileFile) throw new AssistantRefusal("bad-argument", "A profile file is at most 16 MB.");
            }

            // The importers read these bytes, held here, on the interface's
            // thread: the card's scan and the import read one buffer, and no
            // file another program could rewrite while the card waits.
            var import = inv.OnUi(() => inv.App.ReadProfileFile(fileName, bytes));
            var detail = new List<string> { AssistantToolWords.FileLine(fileName) };
            detail.AddRange(import.Asks);
            // An import always asks, whatever it holds.
            var ask = new AssistantAsk(AssistantToolWords.AskImport(import.Name), detail);
            object result = await inv.AskThenAsync(ask, () =>
            {
                var (id, name) = inv.App.AddProfile(import);
                return Done(new ProfileChangeResult { ProfileId = id, Name = OutsideText.Of(name) },
                    import.Report ?? "Added. switch_profile puts it in force.");
            }).ConfigureAwait(false);
            return (ProfileChangeResult)result;
        }

        /// <summary>export_profile: the file to the path given, written with
        /// the program's rights, or its bytes in the result.</summary>
        private static Task<FileResult> ExportProfile(AssistantInvocation inv, ExportProfileArgs a, CancellationToken ct)
        {
            inv.Detail = a.Profile;
            var (bytes, suggested) = inv.OnUi(() => inv.App.ExportProfile(a.Profile));
            if (string.IsNullOrWhiteSpace(a.Path))
                return Task.FromResult(new FileResult { File = suggested, ContentBase64 = Convert.ToBase64String(bytes), Message = "The file's bytes are in content_base64." });
            if (!System.IO.Path.IsPathFullyQualified(a.Path))
                throw new AssistantRefusal("bad-argument", "Give the full path of the file to write.");
            string path = a.Path.EndsWith(PadForge.Common.ProfileTransfer.FileExtension, StringComparison.OrdinalIgnoreCase)
                ? a.Path : a.Path + PadForge.Common.ProfileTransfer.FileExtension;
            try
            {
                AsClient(inv, () => { System.IO.File.WriteAllBytes(path, bytes); return 0; });
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new AssistantRefusal("no-file", "PadForge could not write the file with the user's rights (" + error.GetType().Name.Replace("Exception", string.Empty)
                    + "). Pick a folder the user can write to.");
            }
            return Task.FromResult(new FileResult { File = System.IO.Path.GetFileName(path), Message = "Written." });
        }

        private static async Task<ControllerChangeResult> AddController(AssistantInvocation inv, AddControllerArgs a, CancellationToken ct)
        {
            string type = AssistantNames.Type(a.Type);
            inv.Detail = type;
            var ask = type == "keyboard_mouse"
                ? new AssistantAsk(AssistantToolWords.AskKeyboardMouse(), new[] { AssistantToolWords.KeyboardMouseLine() })
                : null;
            object result = await inv.ChangeAsync(ask, () =>
            {
                int number = inv.App.AddController(type, a.Preset);
                return Done(new ControllerChangeResult { Controller = number }, "Made controller " + number + ". Assign a device with assign_device.");
            }).ConfigureAwait(false);
            return (ControllerChangeResult)result;
        }

        private static Task<ControllerChangeResult> RemoveController(AssistantInvocation inv, ControllerArgs a, CancellationToken ct)
        {
            inv.Detail = Number(a.Controller);
            var result = (ControllerChangeResult)inv.Change(() =>
            {
                inv.App.RemoveController(a.Controller);
                return Done(new ControllerChangeResult { Controller = a.Controller }, "Removed. The other controllers keep their numbers in the sidebar's order, so list them again.");
            });
            return Task.FromResult(result);
        }

        private static async Task<ControllerChangeResult> SetControllerType(AssistantInvocation inv, SetControllerTypeArgs a, CancellationToken ct)
        {
            string type = AssistantNames.Type(a.Type);
            inv.Detail = Number(a.Controller) + " " + type;
            var ask = type == "keyboard_mouse"
                ? new AssistantAsk(AssistantToolWords.AskKeyboardMouse(), new[] { AssistantToolWords.KeyboardMouseLine() })
                : null;
            object result = await inv.ChangeAsync(ask, () =>
            {
                inv.App.SetControllerType(a.Controller, type, a.Preset);
                return Done(new ControllerChangeResult { Controller = a.Controller }, "Changed, and its devices mapped again.");
            }).ConfigureAwait(false);
            return (ControllerChangeResult)result;
        }

        private static async Task<ControllerChangeResult> AssignDevice(AssistantInvocation inv, AssignDeviceArgs a, CancellationToken ct)
        {
            bool assigned = a.Assigned ?? true;
            inv.Detail = a.Device + " " + Number(a.Controller);
            string type = inv.OnUi(() => inv.App.ControllerType(a.Controller))
                ?? throw new AssistantRefusal("unknown-slot", "No virtual controller has that number. list_controllers gives them.");
            var ask = assigned && type == "keyboard_mouse"
                ? new AssistantAsk(AssistantToolWords.AskKeyboardMouse(), new[] { AssistantToolWords.KeyboardMouseLine() })
                : null;
            object result = await inv.ChangeAsync(ask, () =>
            {
                inv.App.AssignDevice(a.Device, a.Controller, assigned, a.Revision);
                return Done(new ControllerChangeResult { Controller = a.Controller },
                    assigned ? "Assigned and mapped. get_mappings shows the rows." : "Taken off.");
            }).ConfigureAwait(false);
            return (ControllerChangeResult)result;
        }

        private static async Task<MappingsChangeResult> SetMappings(AssistantInvocation inv, SetMappingsArgs a, CancellationToken ct)
        {
            inv.Detail = Number(a.Controller);
            var rows = a.Rows ?? new List<MappingEditArgs>();
            if (rows.Count == 0 && !a.ClearAll) throw new AssistantRefusal("bad-argument", "Name at least one row, or clear_all.");
            string type = inv.OnUi(() => inv.App.ControllerType(a.Controller))
                ?? throw new AssistantRefusal("unknown-slot", "No virtual controller has that number. list_controllers gives them.");
            var edits = rows.Select(r => new AssistantMappingEdit
            {
                Target = r.Target, Source = r.Source, Device = r.Device, NegativeSource = r.NegativeSource,
                Inverted = r.Inverted, HalfAxis = r.HalfAxis, DeadZone = r.DeadZone,
            }).ToList();
            AssistantAsk ask = null;
            // A source and a negative source each press a key on a keyboard
            // and mouse controller, and the card names the key as the
            // controller's page does (get_mappings' label), as a layer's
            // Paste names its keys (AssistantCatalog.RowKeyLines).
            if (type == "keyboard_mouse" && edits.Any(e => !string.IsNullOrEmpty(e.Source) || !string.IsNullOrEmpty(e.NegativeSource)))
            {
                var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in inv.OnUi(() => inv.App.ReadMappings(a.Controller)))
                    if (!string.IsNullOrEmpty(row.Target) && !string.IsNullOrEmpty(row.Label)) labels.TryAdd(row.Target, row.Label);
                var lines = new List<string>();
                foreach (var e in edits)
                {
                    string key = e.Target != null && labels.TryGetValue(e.Target, out string label) ? label : e.Target;
                    foreach (string source in new[] { e.Source, e.NegativeSource })
                    {
                        if (string.IsNullOrEmpty(source)) continue;
                        string line = AssistantToolWords.KeyLine(key, source);
                        if (!lines.Contains(line)) lines.Add(line);
                    }
                }
                ask = new AssistantAsk(AssistantToolWords.AskKeyboardMouse(), lines);
            }
            object result = await inv.ChangeAsync(ask, () =>
            {
                // Every row cleared first, as the page's Clear All does, then
                // the rows given, as one run of changes undo takes back whole.
                if (a.ClearAll) inv.App.ClearMappings(a.Controller);
                if (edits.Count > 0) inv.App.SetMappings(a.Controller, edits);
                var names = new HashSet<string>(edits.Select(e => e.Target), StringComparer.OrdinalIgnoreCase);
                return Done(new MappingsChangeResult
                {
                    Controller = a.Controller,
                    Rows = inv.App.ReadMappings(a.Controller).Where(r => names.Contains(r.Target)).ToList(),
                }, edits.Count == 0 ? "Every row is clear." : "Written. The rows below are as they read now.");
            }).ConfigureAwait(false);
            return (MappingsChangeResult)result;
        }

        private static async Task<MappingsChangeResult> AutoMap(AssistantInvocation inv, ControllerArgs a, CancellationToken ct)
        {
            inv.Detail = Number(a.Controller);
            string type = inv.OnUi(() => inv.App.ControllerType(a.Controller))
                ?? throw new AssistantRefusal("unknown-slot", "No virtual controller has that number. list_controllers gives them.");
            var ask = type == "keyboard_mouse"
                ? new AssistantAsk(AssistantToolWords.AskKeyboardMouse(), new[] { AssistantToolWords.KeyboardMouseLine() })
                : null;
            object result = await inv.ChangeAsync(ask, () =>
            {
                inv.App.AutoMap(a.Controller);
                return Done(new MappingsChangeResult { Controller = a.Controller, Rows = inv.App.ReadMappings(a.Controller) }, "Mapped.");
            }).ConfigureAwait(false);
            return (MappingsChangeResult)result;
        }

        private static async Task<MacroChangeResult> SaveMacro(AssistantInvocation inv, SaveMacroArgs a, CancellationToken ct)
        {
            inv.Detail = Number(a.Controller);
            var macro = a.Macro.Clone();
            var lines = inv.OnUi(() => inv.App.ScanMacro(macro));
            var ask = lines.Count > 0 ? new AssistantAsk(AssistantToolWords.AskMacro(), lines) : null;
            object result = await inv.ChangeAsync(ask, () =>
            {
                int index = inv.App.SaveMacro(a.Controller, a.Index, macro);
                return Done(new MacroChangeResult { Controller = a.Controller, Index = index }, "Saved.");
            }).ConfigureAwait(false);
            return (MacroChangeResult)result;
        }

        private static Task<MacroChangeResult> DeleteMacro(AssistantInvocation inv, DeleteMacroArgs a, CancellationToken ct)
        {
            inv.Detail = Number(a.Controller);
            var result = (MacroChangeResult)inv.Change(() =>
            {
                inv.App.DeleteMacro(a.Controller, a.Index);
                return Done(new MacroChangeResult { Controller = a.Controller, Index = a.Index }, "Deleted. The macros after it moved up one.");
            });
            return Task.FromResult(result);
        }

        private static Task<UndoResult> Undo(AssistantInvocation inv, NoArguments _, CancellationToken ct)
        {
            var undo = inv.Calls.Undo ?? throw new AssistantRefusal("not-available", "PadForge keeps no undo here.");
            string back = inv.OnUi(() => undo.UndoNewestOf(inv.Caller.Key));
            return Task.FromResult(Done(new UndoResult { PutBack = back }, "Undone."));
        }
    }

    /// <summary>The names an assistant's tools use for PadForge's own
    /// kinds, fixed ASCII.</summary>
    internal static class AssistantNames
    {
        internal static readonly string[] ControllerTypes =
            { "xbox", "playstation", "nintendo", "extended", "keyboard_mouse", "midi", "vr" };

        /// <summary>A type as an assistant names it, refused when it is
        /// none.</summary>
        internal static string Type(string word)
        {
            string w = (word ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            if (w is "keyboardmouse" or "keyboard_and_mouse" or "kbm") w = "keyboard_mouse";
            if (Array.IndexOf(ControllerTypes, w) < 0)
                throw new AssistantRefusal("bad-argument", "A controller's type is xbox, playstation, nintendo, extended, keyboard_mouse, midi or vr.");
            return w;
        }

        internal static string Of(VirtualControllerType type) => type switch
        {
            VirtualControllerType.Xbox => "xbox",
            VirtualControllerType.PlayStation => "playstation",
            VirtualControllerType.Nintendo => "nintendo",
            VirtualControllerType.Extended => "extended",
            VirtualControllerType.KeyboardMouse => "keyboard_mouse",
            VirtualControllerType.Midi => "midi",
            VirtualControllerType.Vr => "vr",
            _ => type.ToString().ToLowerInvariant(),
        };

        /// <summary>A type as the pipe words it (AutomationCommands.Word),
        /// such as keyboard-mouse, in the word the tools use for it, such as
        /// keyboard_mouse.</summary>
        internal static string FromPipe(string word) => word?.Replace('-', '_');

        internal static VirtualControllerType ToType(string word) => Type(word) switch
        {
            "playstation" => VirtualControllerType.PlayStation,
            "nintendo" => VirtualControllerType.Nintendo,
            "extended" => VirtualControllerType.Extended,
            "keyboard_mouse" => VirtualControllerType.KeyboardMouse,
            "midi" => VirtualControllerType.Midi,
            "vr" => VirtualControllerType.Vr,
            _ => VirtualControllerType.Xbox,
        };
    }
}
