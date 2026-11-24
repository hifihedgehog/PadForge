namespace PadForge.Services.Automation
{
    /// <summary>
    /// How much outside programs may do while Automation is on (#597). Each
    /// level allows what the ones before it allow.
    ///
    /// <para>Saved by its number in PadForge.xml. A member keeps its number
    /// for good, and a new one goes at the end.</para>
    /// </summary>
    public enum AutomationLevel
    {
        /// <summary>Switch profiles and release them, start and stop the
        /// engine, turn virtual controllers on and off, show and hide the
        /// window, close PadForge, save a diagnostics snapshot, and ask what
        /// is running. What a launcher or a command line needs.</summary>
        Commands = 0,

        /// <summary>Commands, and: see devices, virtual controllers,
        /// mappings, profiles and settings, follow the inputs of every
        /// device, and show the user a place in the interface.</summary>
        Read = 1,

        /// <summary>Read, and: add devices of its own and drive them, hold a
        /// control of any device, and hand named values to macro
        /// triggers.</summary>
        SendInputs = 2,

        /// <summary>Send Inputs, and: change what a person can change on the
        /// pages.</summary>
        ChangeSettings = 3,
    }

    /// <summary>The level's two other forms: the number the settings file
    /// holds and the word a program reads.</summary>
    internal static class AutomationLevels
    {
        /// <summary>The level a saved number names. A number no level has,
        /// as a hand-edited file or a newer build's file can hold, reads as
        /// the lowest: a file never raises the level past what it says.</summary>
        internal static AutomationLevel FromSaved(int saved)
            => saved >= (int)AutomationLevel.Commands && saved <= (int)AutomationLevel.ChangeSettings
                ? (AutomationLevel)saved
                : AutomationLevel.Commands;

        /// <summary>The level as the state line names it. Fixed ASCII, never
        /// localized.</summary>
        internal static string Word(AutomationLevel level) => level switch
        {
            AutomationLevel.Read => "read",
            AutomationLevel.SendInputs => "send-inputs",
            AutomationLevel.ChangeSettings => "change-settings",
            _ => "commands",
        };
    }
}
