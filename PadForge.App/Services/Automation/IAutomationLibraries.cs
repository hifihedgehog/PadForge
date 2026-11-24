using System.Collections.Generic;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The script libraries PadForge carries, as the Automation page's Save
    /// a Library button writes them (#597). The page asks for the
    /// libraries, shows one Save dialog that offers each as a file type,
    /// and has the one the user picked written where the user said.
    /// <see cref="AutomationLibraries"/> is the one the app hands the host.
    /// </summary>
    internal interface IAutomationLibraries
    {
        /// <summary>The libraries in the order the dialog lists them, each
        /// with the name and file type the dialog shows and the file name it
        /// suggests. The first is the one the dialog starts on.</summary>
        IReadOnlyList<AutomationLibrary> All { get; }

        /// <summary>Writes the library of <paramref name="kind"/> to
        /// <paramref name="path"/>, replacing a file that is there, with the
        /// user's own rights. Waits for the install, so it is called off the
        /// interface's thread. A failure throws.</summary>
        void Save(AutomationLibraryKind kind, string path);
    }
}
