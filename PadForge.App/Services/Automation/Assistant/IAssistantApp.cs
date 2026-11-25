using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// What an assistant's tools act on (#597): PadForge as its pages drive
    /// it. The main window implements it for the app (MainWindow.Assistant),
    /// through the same calls the pages make, and a test stands a recorder in
    /// its place. Every member is called on the interface's thread unless its
    /// summary says otherwise. A member that cannot do what it is asked throws
    /// <see cref="AssistantRefusal"/> with words the assistant can act on.
    /// </summary>
    internal interface IAssistantApp
    {
        // ── Read ──

        AssistantSystemResult ReadSystem();

        List<AssistantProfile> ListProfiles();

        /// <summary>A profile by ID or name. Refuses <c>unknown-profile</c>.</summary>
        AssistantProfileDetail ReadProfile(string profile);

        List<AssistantController> ListControllers();

        /// <summary>What a controller sends to the game now, or null while
        /// the engine is stopped. Called on a worker.</summary>
        Dictionary<string, string> Outputs(int number);

        /// <summary>The presets a type offers, in the picker's order.</summary>
        List<AssistantPreset> Presets(string type);

        /// <summary>Every row of a controller. Refuses
        /// <c>unknown-slot</c>.</summary>
        List<AssistantMappingRow> ReadMappings(int number);

        List<AssistantMacro> ReadMacros(int number);

        List<AssistantMenu> ReadMenus(int number);

        /// <summary>The revision of the assignment of devices to
        /// controllers, which a change made against an older one is refused
        /// for. Called on any thread.</summary>
        long Revision { get; }

        /// <summary>Where the window is now.</summary>
        AssistantWindowPlace WindowPlace();

        /// <summary>Brings the window forward, after the user picked Show on
        /// the question card (#600). Never at a program's word.</summary>
        void BringForward();

        /// <summary>The type of a controller by number, for the scanner, or
        /// null.</summary>
        string ControllerType(int number);

        // ── Profiles ──

        /// <summary>Makes a profile, empty or a copy of another, and gives
        /// its ID.</summary>
        string CreateProfile(string name, string copyOf);

        void RenameProfile(string profile, string name);

        void DeleteProfile(string profile);

        /// <summary>Reads a profile file into what it would add, without
        /// adding it. Called inside the program's own rights.</summary>
        AssistantImport ReadProfileFile(string path);

        /// <summary>Adds a profile <see cref="ReadProfileFile"/> read, and
        /// gives its ID and the name it took.</summary>
        (string Id, string Name) AddProfile(AssistantImport import);

        /// <summary>A profile as its file holds it, read on the interface's
        /// thread, with the file name it suggests.</summary>
        (byte[] Bytes, string FileName) ExportProfile(string profile);

        // ── Virtual controllers ──

        /// <summary>Makes a controller and gives its number.</summary>
        int AddController(string type, string preset);

        void RemoveController(int number);

        void SetControllerType(int number, string type, string preset);

        // ── Devices ──

        /// <summary>Assigns a device to a controller, or takes it off.
        /// Refuses <c>stale</c> when <paramref name="revision"/> is not the
        /// one in force.</summary>
        void AssignDevice(string device, int number, bool assigned, long? revision);

        // ── Mappings ──

        void SetMappings(int number, IReadOnlyList<AssistantMappingEdit> rows);

        /// <summary>Clears the rows named, or every row for none.</summary>
        void ClearMappings(int number, IReadOnlyList<string> targets);

        /// <summary>Maps the controller as assigning its devices does.</summary>
        void AutoMap(int number);

        // ── Macros ──

        /// <summary>Adds a macro, or replaces the one at
        /// <paramref name="index"/>, from PadForge's JSON form. Gives its
        /// index.</summary>
        int SaveMacro(int number, int? index, JsonElement macro);

        void DeleteMacro(int number, int index);

        /// <summary>What a macro in PadForge's JSON form would do that
        /// needs the user's answer, line by line, or an empty list.</summary>
        List<string> ScanMacro(JsonElement macro);

        // ── Undo ──

        /// <summary>The state a program's changes can be undone to.</summary>
        AssistantUndoState TakeUndoState();

        /// <summary>Puts a state back as one step, and says what came
        /// back.</summary>
        string RestoreUndoState(AssistantUndoState state);

        // ── Problem recorder ──

        /// <summary>Records for the given time and gives the scrubbed
        /// summary. Called on a worker: it waits.</summary>
        System.Threading.Tasks.Task<AssistantRecording> RecordAsync(int seconds, string note, System.Threading.CancellationToken stopping);

        // ── Privacy ──

        /// <summary>A scrubber that knows this PC's private values: the user
        /// and PC names, folders, serial numbers, Bluetooth addresses and
        /// access codes. Called on a worker.</summary>
        PadForge.Engine.Privacy.PrivacyScrubber MakeScrubber();
    }

    /// <summary>One row a set_mappings call writes.</summary>
    internal sealed class AssistantMappingEdit
    {
        public string Target { get; set; }
        public string Source { get; set; }
        public string Device { get; set; }
        public string NegativeSource { get; set; }
        public bool? Inverted { get; set; }
        public bool? HalfAxis { get; set; }
        public int? DeadZone { get; set; }
    }

    /// <summary>A profile file read and not yet added: the profile, and the
    /// lines that say what it would do that needs the user's answer.</summary>
    internal sealed class AssistantImport
    {
        public object Profile { get; init; }
        public string Name { get; init; }
        public List<string> Asks { get; init; } = new();

        /// <summary>The importer's own words on what came over, for a file
        /// of another program.</summary>
        public string Report { get; init; }
    }

    /// <summary>What undo puts back: opaque to everything but the app that
    /// took it.</summary>
    internal sealed class AssistantUndoState
    {
        public object Profile { get; init; }
        public object AppSettings { get; init; }
        public object Profiles { get; init; }
        public object DefaultSnapshot { get; init; }
        public string ActiveProfileId { get; init; }
        public DateTimeOffset Taken { get; init; }
    }

    /// <summary>A recording a program asked for.</summary>
    internal sealed class AssistantRecording
    {
        public string Summary { get; init; }
    }
}
