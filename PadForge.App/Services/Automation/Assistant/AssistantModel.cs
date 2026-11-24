using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;

namespace PadForge.Services.Automation
{
    // The shapes PadForge's own things take in an assistant's results
    // (#597). Names are lowercase words joined by underscores on the wire
    // (AssistantJson). Every name or text that came from outside PadForge is
    // an OutsideText.

    internal sealed class AssistantDriverState
    {
        [Description("The driver or program, by PadForge's name for it.")]
        public string Name { get; set; }

        [Description("Whether it is installed.")]
        public bool Installed { get; set; }

        [Description("Its version when PadForge knows it.")]
        public string Version { get; set; }

        [Description("What it does for PadForge.")]
        public string For { get; set; }
    }

    internal sealed class AssistantSystemResult
    {
        [Description("How many times a second the engine reads the devices and writes the virtual controllers.")]
        public int PollingRateHz { get; set; }

        [Description("The rate the engine runs at now, which a profile can override, or 0 while it is stopped.")]
        public double RunningAtHz { get; set; }

        [Description("The drivers and helper programs PadForge works with.")]
        public List<AssistantDriverState> Drivers { get; set; } = new();

        [Description("Whether HidHide is installed, which hides a physical device from games so they see only the virtual controller.")]
        public bool HidHideInstalled { get; set; }

        [Description("How many devices HidHide hides now.")]
        public int DevicesHidden { get; set; }
    }

    internal sealed class AssistantProfileController
    {
        [Description("The controller's number in the profile.")]
        public int Number { get; set; }

        [Description("Its type: xbox, playstation, nintendo, extended, keyboard_mouse, midi or vr.")]
        public string Type { get; set; }

        [Description("Its preset, the model it presents to games.")]
        public string Preset { get; set; }

        [Description("Whether it is turned on.")]
        public bool On { get; set; }

        [Description("The devices assigned to it, by name.")]
        public List<OutsideText> Devices { get; set; } = new();
    }

    internal sealed class AssistantProfile
    {
        [Description("The profile's ID, which every tool takes in place of its name.")]
        public string Id { get; set; }

        [Description("The profile's name.")]
        public OutsideText Name { get; set; }

        [Description("Whether it is the profile in force now.")]
        public bool Active { get; set; }

        [Description("Whether it is the default profile, the one in force when no other is.")]
        public bool Default { get; set; }

        [Description("The game files that switch to it when they come to the front.")]
        public List<OutsideText> Games { get; set; } = new();

        [Description("How many virtual controllers it makes.")]
        public int Controllers { get; set; }
    }

    internal sealed class AssistantProfileDetail
    {
        public string Id { get; set; }
        public OutsideText Name { get; set; }
        public bool Active { get; set; }
        public bool Default { get; set; }
        public List<OutsideText> Games { get; set; } = new();

        [Description("The polling rate it sets while it is in force, or 0 when it keeps the app's.")]
        public int PollingRateHz { get; set; }

        [Description("The virtual controllers it makes, with their devices.")]
        public List<AssistantProfileController> Controllers { get; set; } = new();

        [Description("How many macros it holds in all.")]
        public int Macros { get; set; }

        [Description("The extras it turns on while it is in force.")]
        public List<string> ExtrasOn { get; set; } = new();
    }

    internal sealed class AssistantControllerDevice
    {
        [Description("The device's short ID.")]
        public string Id { get; set; }

        public OutsideText Name { get; set; }

        [Description("Whether the device is connected.")]
        public bool Online { get; set; }
    }

    internal sealed class AssistantController
    {
        [Description("The controller's number, as the sidebar shows it. Tools take this number.")]
        public int Number { get; set; }

        [Description("Its type: xbox, playstation, nintendo, extended, keyboard_mouse, midi or vr.")]
        public string Type { get; set; }

        [Description("Its preset, the model it presents to games.")]
        public string Preset { get; set; }

        [Description("Its name as the interface shows it.")]
        public string Label { get; set; }

        [Description("Whether it is turned on.")]
        public bool On { get; set; }

        [Description("The devices assigned to it.")]
        public List<AssistantControllerDevice> Devices { get; set; } = new();

        [Description("What it sends to the game now, by control. Present when asked for and the engine runs.")]
        public Dictionary<string, string> Outputs { get; set; }
    }

    internal sealed class AssistantPreset
    {
        [Description("The preset's ID, which add_controller and set_controller_type take.")]
        public string Id { get; set; }

        [Description("The preset's name.")]
        public string Name { get; set; }
    }

    internal sealed class AssistantMappingSource
    {
        [Description("The input, as PadForge writes it: Button 0, Axis 1, Slider 0, POV 0 Up. An I in front reads it the other way round, an H one half of an axis.")]
        public string Input { get; set; }

        [Description("The short ID of the device the input belongs to, or empty for the controller's first device.")]
        public string Device { get; set; }
    }

    internal sealed class AssistantMappingRow
    {
        [Description("The control of the virtual controller the row feeds. set_mappings takes it.")]
        public string Target { get; set; }

        [Description("The row's name as the interface shows it.")]
        public string Label { get; set; }

        [Description("The input the row reads, or empty when nothing is mapped.")]
        public string Source { get; set; }

        [Description("The short ID of the source's device, or empty.")]
        public string SourceDevice { get; set; }

        [Description("The source as the interface names it, such as the button's name on that device.")]
        public OutsideText SourceName { get; set; }

        [Description("The input that drives the other direction of an axis row, or empty.")]
        public string NegativeSource { get; set; }

        [Description("Whether the input is read the other way round.")]
        public bool Inverted { get; set; }

        [Description("Whether only one half of the axis is read.")]
        public bool HalfAxis { get; set; }

        [Description("The dead zone in percent, for inputs that have one.")]
        public int DeadZone { get; set; }

        [Description("More inputs the row combines with the first.")]
        public List<AssistantMappingSource> MoreSources { get; set; } = new();

        [Description("How the inputs are combined when there is more than one.")]
        public string Combine { get; set; }
    }

    internal sealed class AssistantMacro
    {
        [Description("The macro's position in its controller's list, from 0.")]
        public int Index { get; set; }

        public OutsideText Name { get; set; }

        [Description("What starts it, in a few words.")]
        public string Trigger { get; set; }

        [Description("How many steps it has.")]
        public int Steps { get; set; }

        [Description("The kinds of its steps, in order.")]
        public List<string> StepKinds { get; set; } = new();

        [Description("The macro in PadForge's own JSON form, the one save_macro takes.")]
        public JsonElement Data { get; set; }
    }

    internal sealed class AssistantMenu
    {
        public OutsideText Name { get; set; }

        [Description("How many items it holds.")]
        public int Items { get; set; }
    }

    /// <summary>What a change needs the user's answer for, as the scanner
    /// found it: one line each for the request card.</summary>
    internal sealed class AssistantAsk
    {
        public AssistantAsk(string title, IReadOnlyList<string> detail)
        {
            Title = title;
            Detail = detail;
        }

        public string Title { get; }
        public IReadOnlyList<string> Detail { get; }
    }

    /// <summary>Where PadForge's window is, as show says it.</summary>
    internal enum AssistantWindowPlace
    {
        InFront,
        Behind,
        Minimized,
        InTray,
    }
}
