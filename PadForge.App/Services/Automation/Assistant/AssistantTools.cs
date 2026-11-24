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
    //  Arguments and results (#597)
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

    internal sealed class ProfilesResult
    {
        public List<AssistantProfile> Profiles { get; set; } = new();
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

        [Description("The value an axis rests at, when PadForge knows it.")]
        public int? Rest { get; set; }

        [Description("The input as set_mappings takes it: Button 3, Axis 1, Slider 0, POV 0 Up.")]
        public string Input { get; set; }
    }

    internal sealed class DeviceState
    {
        [Description("The indexes of the buttons held down.")]
        public List<int> ButtonsDown { get; set; } = new();

        [Description("Every axis, 0 to 65535, a stick at rest on 32768.")]
        public List<int> Axes { get; set; } = new();

        [Description("Every slider, 0 to 65535.")]
        public List<int> Sliders { get; set; } = new();

        [Description("Every hat, in hundredths of a degree, or -1 at rest.")]
        public List<int> Hats { get; set; } = new();

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

        [Description("The feedback it takes: rumble, trigger rumble, light, player number.")]
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

        [Description("The value it moved to.")]
        public int Value { get; set; }

        public string Message { get; set; }
    }

    internal sealed class ControllerArgs
    {
        [JsonRequired, Description("The controller's number, from list_controllers.")]
        public int Controller { get; set; }
    }

    internal sealed class MappingsResult
    {
        public int Controller { get; set; }
        public List<AssistantMappingRow> Rows { get; set; } = new();
    }

    internal sealed class MacrosResult
    {
        public int Controller { get; set; }
        public List<AssistantMacro> Macros { get; set; } = new();
        public List<AssistantMenu> Menus { get; set; } = new();
    }

    internal sealed class RecordArgs
    {
        [Description("How long to record, 3 to 60 seconds. 10 when left out. Ask the user to show the problem meanwhile.")]
        public int? Seconds { get; set; }

        [Description("A note PadForge marks the recording with at its start.")]
        public string Note { get; set; }
    }

    internal sealed class RecordResult
    {
        [Description("The recording's file, beside PadForge.exe. The user attaches it to a bug report.")]
        public string File { get; set; }

        [Description("What the recorder found, scrubbed of private values.")]
        public string Summary { get; set; }
    }

    internal sealed class FindSettingsArgs
    {
        [JsonRequired, Description("Words from the setting's name or where it is, in English or the interface language.")]
        public string Words { get; set; }

        [Description("How many to return, 1 to 50. 15 when left out.")]
        public int? Most { get; set; }
    }

    internal sealed class SettingsResult
    {
        public List<AssistantSetting> Settings { get; set; } = new();
    }

    internal sealed class SettingArgs
    {
        [JsonRequired, Description("The setting's ID, from find_settings.")]
        public string Id { get; set; }
    }

    internal sealed class SetSettingArgs
    {
        [JsonRequired, Description("The setting's ID, from find_settings.")]
        public string Id { get; set; }

        [JsonRequired, Description("The new value: a number, true or false, or one of the choices' text.")]
        public JsonElement Value { get; set; }
    }

    internal sealed class SetSettingResult : AssistantChangeResult
    {
        public AssistantSetting Setting { get; set; }
    }

    internal sealed class ShowArgs
    {
        [Description("A setting's ID.")]
        public string Setting { get; set; }

        [Description("A device's short ID.")]
        public string Device { get; set; }

        [Description("A controller's number.")]
        public int? Controller { get; set; }

        [Description("A profile's ID or name.")]
        public string Profile { get; set; }
    }

    internal sealed class AxisDeclaration
    {
        public string Name { get; set; }

        [Description("Where it rests, 0 to 65535. 32768 for an axis and 0 for a slider when left out.")]
        public int? Rest { get; set; }
    }

    internal enum AssistantFeedback { Rumble, TriggerRumble, Light, Player }

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

        [Description("Axes and sliders by index, 0 to 65535. Sliders are 24 to 31.")]
        public Dictionary<string, int> Axes { get; set; }

        [Description("Hats by index, in hundredths of a degree, or -1 at rest.")]
        public Dictionary<string, int> Hats { get; set; }
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

        [Description("The value to hold it at: 1 or 0 for a button, 0 to 65535 for an axis, hundredths of a degree or -1 for a hat. Leave it out with release.")]
        public int? Value { get; set; }

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

        [Description("In place of path: the file's bytes in base64.")]
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

        [JsonRequired, Description("The rows to write. Rows not named stay as they are.")]
        public List<MappingEditArgs> Rows { get; set; }
    }

    internal sealed class ClearMappingsArgs
    {
        [JsonRequired, Description("The controller's number.")]
        public int Controller { get; set; }

        [Description("The targets of the rows to clear. Every row when left out.")]
        public List<string> Targets { get; set; }
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

        [Description("The index of the macro to replace, from list_macros. A new macro when left out.")]
        public int? Index { get; set; }

        [JsonRequired, Description("The macro in PadForge's own JSON form, as list_macros gives a macro's data.")]
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

        [JsonRequired, Description("The macro's index, from list_macros.")]
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

    internal sealed class ChangeOnlyResult : AssistantChangeResult { }

    // ─────────────────────────────────────────────
    //  The tools
    // ─────────────────────────────────────────────

    /// <summary>
    /// PadForge's tools for assistants (#597), in the order every list keeps,
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

        private static IReadOnlyList<AssistantToolDefinition> Build() => new[]
        {
            // ── Commands ──
            Tool<NoArguments, StatusResult>("status", "PadForge's Status",
                "Says what PadForge is doing: the engine, the profile in force, the window, each virtual controller and what the Automation level allows. Call it first, and again when a call is refused.",
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
            Tool<NoArguments, FileResult>("save_snapshot", "Save a Diagnostics Snapshot",
                "Saves a diagnostics snapshot beside PadForge.exe, as the Settings page's Save Snapshot does, for a bug report.",
                AutomationLevel.Commands, false, false, false, SaveSnapshot),

            // ── Read ──
            Tool<NoArguments, AssistantSystemResult>("get_system", "Polling Rate and Drivers",
                "Reads the polling rate, the drivers PadForge works with and whether HidHide is installed. Use it when a game does not see a controller.",
                AutomationLevel.Read, true, false, true, (inv, _, _) => Task.FromResult(inv.OnUi(inv.App.ReadSystem))),
            Tool<NoArguments, ProfilesResult>("list_profiles", "List Profiles",
                "Lists the profiles with their IDs, names, games and which one is in force.",
                AutomationLevel.Read, true, false, true, (inv, _, _) => Task.FromResult(new ProfilesResult { Profiles = inv.OnUi(inv.App.ListProfiles) })),
            Tool<ProfileArgs, AssistantProfileDetail>("get_profile", "What a Profile Holds",
                "Reads one profile: its virtual controllers with their devices, its games, its polling rate and the extras it turns on.",
                AutomationLevel.Read, true, false, true, (inv, a, _) => { inv.Detail = a.Profile; return Task.FromResult(inv.OnUi(() => inv.App.ReadProfile(a.Profile))); }),
            Tool<ListControllersArgs, ControllersResult>("list_controllers", "List Virtual Controllers",
                "Lists the virtual controllers games see, with their numbers, types, presets and devices, and on request what each sends to the game now and the presets of each type.",
                AutomationLevel.Read, true, false, true, ListControllers),
            Tool<ListDevicesArgs, DevicesResult>("list_devices", "List Devices",
                "Lists the input devices PadForge reads, keyboards and mice included, each by product name and a short ID, with a filter by kind, assignment, connection or words.",
                AutomationLevel.Read, true, false, true, (inv, a, _) => Task.FromResult(ListDevices(inv, a))),
            Tool<DeviceArgs, DeviceDetail>("get_device", "A Device's Controls",
                "Reads one device: each button, axis, slider and hat by index and name with the input set_mappings takes for it, its features and feedback, and on request its state now.",
                AutomationLevel.Read, true, false, true, (inv, a, _) => Task.FromResult(GetDevice(inv, a))),
            Tool<WaitForInputArgs, InputResult>("wait_for_input", "Wait for the User's Input",
                "Waits for the next button, hat or axis the user moves on any device, or on one, and says what it was. Ask the user to press the control they mean, then call this.",
                AutomationLevel.Read, true, false, false, WaitForInput),
            Tool<ControllerArgs, MappingsResult>("get_mappings", "A Controller's Mappings",
                "Reads every mapping row of a virtual controller: its target, the input that feeds it, the device and the row's options.",
                AutomationLevel.Read, true, false, true, (inv, a, _) =>
                {
                    inv.Detail = Number(a.Controller);
                    return Task.FromResult(new MappingsResult { Controller = a.Controller, Rows = inv.OnUi(() => inv.App.ReadMappings(a.Controller)) });
                }),
            Tool<ControllerArgs, MacrosResult>("list_macros", "A Controller's Macros and Menus",
                "Lists a virtual controller's macros, each with its trigger, its steps and its data, and its menus.",
                AutomationLevel.Read, true, false, true, (inv, a, _) =>
                {
                    inv.Detail = Number(a.Controller);
                    return Task.FromResult(inv.OnUi(() => new MacrosResult
                    {
                        Controller = a.Controller,
                        Macros = inv.App.ReadMacros(a.Controller),
                        Menus = inv.App.ReadMenus(a.Controller),
                    }));
                }),
            Tool<RecordArgs, RecordResult>("record_problem", "Record a Problem",
                "Records what the devices and virtual controllers do for a few seconds, as the problem recorder does, while the user shows the problem. Returns the file for a bug report and what the recorder found.",
                AutomationLevel.Read, false, false, false, RecordProblem),
            Tool<FindSettingsArgs, SettingsResult>("find_settings", "Find Settings",
                "Finds settings on every page by words, with each one's ID, name, place, value now, kind and range or choices. Use it before get_setting, set_setting and show.",
                AutomationLevel.Read, true, false, true, (inv, a, _) =>
                {
                    inv.Detail = a.Words;
                    int most = Math.Clamp(a.Most ?? 15, 1, 50);
                    return Task.FromResult(new SettingsResult { Settings = inv.OnUi(() => inv.Settings.Find(a.Words, most)) });
                }),
            Tool<SettingArgs, AssistantSetting>("get_setting", "Read a Setting",
                "Reads one setting by its ID: its value now, its kind and its range or choices.",
                AutomationLevel.Read, true, false, true, (inv, a, _) => { inv.Detail = a.Id; return Task.FromResult(inv.OnUi(() => inv.Settings.Get(a.Id))); }),
            Tool<ShowArgs, AssistantShown>("show", "Show the User Where",
                "Opens PadForge's page on a setting, a device, a controller or a profile and rings it, so the user sees it. It changes nothing and never brings the window up. The result says where the window is, so tell the user where to look.",
                AutomationLevel.Read, true, false, true, (inv, a, _) => Task.FromResult(Show(inv, a))),

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

            // ── Change Settings ──
            Tool<SetSettingArgs, SetSettingResult>("set_setting", "Change a Setting",
                "Changes one setting by its ID, through the control on its page, so it is checked and saved as a person's change is. A value out of range is refused with the range. Some settings ask the user first.",
                AutomationLevel.ChangeSettings, false, true, true, SetSetting, changes: true),
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
                "Writes mapping rows of a virtual controller: which input of which device feeds each target, and the row's options. Rows not named stay. On a keyboard and mouse controller the user is asked first.",
                AutomationLevel.ChangeSettings, false, true, true, SetMappings, changes: true),
            Tool<ClearMappingsArgs, MappingsChangeResult>("clear_mappings", "Clear Mapping Rows",
                "Clears mapping rows of a virtual controller, or every row.",
                AutomationLevel.ChangeSettings, false, true, true, ClearMappings, changes: true),
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

        private static string Number(int controller) => controller.ToString(CultureInfo.InvariantCulture);

        // ─────────────────────────────────────────────
        //  Commands, through the core's executor
        // ─────────────────────────────────────────────

        /// <summary>Runs a line of the command set and gives what followed
        /// its ok, or refuses with the error word's meaning.</summary>
        private static string Command(AssistantInvocation inv, string line)
        {
            var answer = inv.Host.Commands.Answer(line, inv.Stopping);
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
                AutomationLevel = (inv.Host.Level switch
                {
                    AutomationLevel.Read => "read",
                    AutomationLevel.SendInputs => "send_inputs",
                    AutomationLevel.ChangeSettings => "change_settings",
                    _ => "commands",
                }),
                LevelAllows = AssistantGuidance.LevelAllows(inv.Host.Level),
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
                        Type = c.TryGetProperty("type", out var ct2) ? ct2.GetString() : null,
                        On = c.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True,
                        Devices = c.TryGetProperty("devices", out var d) ? d.GetInt32() : 0,
                        Online = c.TryGetProperty("online", out var o) ? o.GetInt32() : 0,
                    });
                }
            }
            if (result.Engine == "stopped") result.Message = "The engine is stopped, so games see no virtual controller. set_engine starts it.";
            return Task.FromResult(result);
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

        private static Task<FileResult> SaveSnapshot(AssistantInvocation inv, NoArguments _, CancellationToken ct)
        {
            string file = Command(inv, "snapshot");
            return Task.FromResult(new FileResult { File = file, Message = "The snapshot is saved beside PadForge.exe, where the user finds it." });
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
                    controller.Outputs = inv.App.Outputs(controller.Number);
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
                    if (!string.IsNullOrWhiteSpace(a.Words) && !Matches(summary.Name?.Text, a.Words)) continue;
                    result.Devices.Add(summary);
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
                            Rest = int.TryParse(r.Get("rest"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rest) ? rest : null,
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
                        break;
                }
            }
            if (a.Live && ud.IsOnline && AutomationDevices.CopyState(ud) is { } state)
                detail.State = StateOf(state, Math.Max(ud.CapButtonCount, CustomInputState.MaxButtons));
            return detail;
        }

        private static DeviceState StateOf(CustomInputState s, int buttons)
        {
            var state = new DeviceState { Battery = s.BatteryPercent, Charging = s.BatteryCharging };
            for (int i = 0; i < Math.Min(buttons, s.Buttons.Length); i++) if (s.Buttons[i]) state.ButtonsDown.Add(i);
            state.Axes.AddRange(s.Axis);
            state.Sliders.AddRange(s.Sliders);
            state.Hats.AddRange(s.Povs);
            return state;
        }

        /// <summary>The waiting step of wait_for_input: how often the
        /// devices are compared, and how far an axis moves before it
        /// counts.</summary>
        internal const int WaitStepMs = 10, AxisMoveCounts = 16384;

        private static async Task<InputResult> WaitForInput(AssistantInvocation inv, WaitForInputArgs a, CancellationToken ct)
        {
            int seconds = Math.Clamp(a.Seconds ?? 15, 1, 60);
            inv.Detail = a.Device;
            UserDevice only = a.Device == null ? null : DeviceBy(inv, a.Device);
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
                Value = value,
            };
        }

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

        private static async Task<RecordResult> RecordAsync(AssistantInvocation inv, RecordArgs a, CancellationToken ct)
        {
            int seconds = Math.Clamp(a.Seconds ?? 10, 3, 60);
            inv.Detail = a.Note;
            var recording = await inv.App.RecordAsync(seconds, a.Note, ct).ConfigureAwait(false);
            return new RecordResult { File = recording.File, Summary = recording.Summary };
        }

        private static AssistantShown Show(AssistantInvocation inv, ShowArgs a)
        {
            int given = (a.Setting != null ? 1 : 0) + (a.Device != null ? 1 : 0) + (a.Controller != null ? 1 : 0) + (a.Profile != null ? 1 : 0);
            if (given != 1) throw new AssistantRefusal("bad-argument", "show takes one of setting, device, controller and profile.");
            var target = a.Setting != null ? new AssistantShowTarget { Kind = AssistantShowKind.Setting, Id = a.Setting }
                : a.Device != null ? new AssistantShowTarget { Kind = AssistantShowKind.Device, Id = a.Device }
                : a.Controller != null ? new AssistantShowTarget { Kind = AssistantShowKind.Controller, Id = Number(a.Controller.Value) }
                : new AssistantShowTarget { Kind = AssistantShowKind.Profile, Id = a.Profile };
            inv.Detail = target.Id;
            return inv.OnUi(() => inv.Settings.Show(target));
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
                    _ => "rumble",
                }).Distinct().ToList();
            string json = JsonSerializer.Serialize(declaration);
            var body = new StringBuilder("declare key=").Append(a.Key ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(a.Name)) body.Append(" name=").Append(AutomationLine.Quote(a.Name));
            body.Append(' ').Append(json);
            string answer = await Line(inv, body.ToString()).ConfigureAwait(false);
            string id = answer.StartsWith("id=", StringComparison.Ordinal) ? answer.Substring(3) : answer;
            return new AddDeviceResult
            {
                Device = id,
                Message = "The device is on the Devices page. Assign it to a controller with assign_device, then drive it with set_device_inputs.",
            };
        }

        private static Dictionary<string, object> Axis(AxisDeclaration axis)
        {
            var d = new Dictionary<string, object>();
            if (axis?.Name != null) d["name"] = axis.Name;
            if (axis?.Rest != null) d["rest"] = axis.Rest.Value;
            return d;
        }

        private static async Task<DoneResult> SetDeviceInputs(AssistantInvocation inv, SetDeviceInputsArgs a, CancellationToken ct)
        {
            inv.Detail = a.Device;
            var body = new StringBuilder("s ").Append(a.Device);
            void Add(string prefix, string key, int value)
            {
                if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    throw new AssistantRefusal("bad-argument", "A control's index is a whole number, as in \"3\".");
                body.Append(' ').Append(prefix).Append(index.ToString(CultureInfo.InvariantCulture)).Append('=')
                    .Append(value.ToString(CultureInfo.InvariantCulture));
            }
            if (a.Buttons != null) foreach (var (k, v) in a.Buttons) Add("b", k, v ? 1 : 0);
            if (a.Axes != null) foreach (var (k, v) in a.Axes) Add("a", k, v);
            if (a.Hats != null) foreach (var (k, v) in a.Hats) Add("h", k, v);
            await Line(inv, body.ToString()).ConfigureAwait(false);
            return new DoneResult { Message = "Set." };
        }

        private static async Task<DoneResult> RemoveDevice(AssistantInvocation inv, DeviceOnlyArgs a, CancellationToken ct)
        {
            inv.Detail = a.Device;
            await Line(inv, "gone " + a.Device).ConfigureAwait(false);
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
                await Line(inv, "rel " + a.Device + " " + control).ConfigureAwait(false);
                return new DoneResult { Message = "Released. The control reads the device again." };
            }
            if (a.Value == null) throw new AssistantRefusal("bad-argument", "Give the value to hold the control at, or release.");
            await Line(inv, "own " + a.Device + " " + control + "=" + a.Value.Value.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            return new DoneResult { Message = "Held until it is released or this connection ends." };
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

        private static async Task<SetSettingResult> SetSetting(AssistantInvocation inv, SetSettingArgs a, CancellationToken ct)
        {
            inv.Detail = a.Id;
            var setting = inv.OnUi(() => inv.Settings.Get(a.Id));
            inv.Detail = setting.Name;
            var ask = inv.OnUi(() => inv.Settings.AskFor(a.Id, a.Value));
            var value = a.Value.Clone();
            object result = await inv.ChangeAsync(ask, () =>
            {
                var after = inv.Settings.Set(a.Id, value);
                return Done(new SetSettingResult { Setting = after }, "Set. The page shows the new value.");
            }).ConfigureAwait(false);
            return (SetSettingResult)result;
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
                        var info = new System.IO.FileInfo(a.Path);
                        if (info.Length > LongestProfileFile) throw new System.IO.IOException("too large");
                        return System.IO.File.ReadAllBytes(a.Path);
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

            // PadForge's own copy, which the importers read on the
            // interface's thread.
            string copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForge-import-" + Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(fileName));
            System.IO.File.WriteAllBytes(copy, bytes);
            try
            {
                var import = inv.OnUi(() => inv.App.ReadProfileFile(copy));
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
            finally
            {
                // A question still open needs the copy for as long as it can
                // be answered.
                if (inv.Pending)
                    _ = Task.Delay(inv.Host.Approvals.Wait + TimeSpan.FromSeconds(30)).ContinueWith(_ => Delete(copy), TaskScheduler.Default);
                else Delete(copy);
            }
        }

        private static void Delete(string path)
        {
            try { System.IO.File.Delete(path); } catch { }
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
            if (a.Rows == null || a.Rows.Count == 0) throw new AssistantRefusal("bad-argument", "Name at least one row.");
            string type = inv.OnUi(() => inv.App.ControllerType(a.Controller))
                ?? throw new AssistantRefusal("unknown-slot", "No virtual controller has that number. list_controllers gives them.");
            var edits = a.Rows.Select(r => new AssistantMappingEdit
            {
                Target = r.Target, Source = r.Source, Device = r.Device, NegativeSource = r.NegativeSource,
                Inverted = r.Inverted, HalfAxis = r.HalfAxis, DeadZone = r.DeadZone,
            }).ToList();
            AssistantAsk ask = null;
            if (type == "keyboard_mouse" && edits.Any(e => !string.IsNullOrEmpty(e.Source)))
            {
                var lines = edits.Where(e => !string.IsNullOrEmpty(e.Source))
                    .Select(e => AssistantToolWords.KeyLine(e.Target, e.Source)).ToList();
                ask = new AssistantAsk(AssistantToolWords.AskKeyboardMouse(), lines);
            }
            object result = await inv.ChangeAsync(ask, () =>
            {
                inv.App.SetMappings(a.Controller, edits);
                var names = new HashSet<string>(edits.Select(e => e.Target), StringComparer.OrdinalIgnoreCase);
                return Done(new MappingsChangeResult
                {
                    Controller = a.Controller,
                    Rows = inv.App.ReadMappings(a.Controller).Where(r => names.Contains(r.Target)).ToList(),
                }, "Written. The rows below are as they read now.");
            }).ConfigureAwait(false);
            return (MappingsChangeResult)result;
        }

        private static Task<MappingsChangeResult> ClearMappings(AssistantInvocation inv, ClearMappingsArgs a, CancellationToken ct)
        {
            inv.Detail = Number(a.Controller);
            var result = (MappingsChangeResult)inv.Change(() =>
            {
                inv.App.ClearMappings(a.Controller, a.Targets);
                return Done(new MappingsChangeResult { Controller = a.Controller }, a.Targets == null ? "Every row is clear." : "Cleared.");
            });
            return Task.FromResult(result);
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
