using System.Collections.Generic;

namespace PadForge.Services.Automation
{
    /// <summary>One script library PadForge carries (#597), as the Save
    /// dialog of the Automation page offers it.</summary>
    /// <param name="Name">The language, as the dialog's file types name it,
    /// such as AutoHotkey v2.</param>
    /// <param name="FileName">The name the dialog suggests, with its
    /// extension, such as PadForge.ahk.</param>
    /// <param name="Filter">The dialog's file type for it, in the form a
    /// Windows file dialog takes: the words, a bar, the pattern
    /// (<c>AutoHotkey v2 (*.ahk)|*.ahk</c>).</param>
    internal sealed record AutomationLibraryKind(string Name, string FileName, string Filter);

    /// <summary>
    /// The script libraries PadForge carries, as the Automation page's Save
    /// a Library button writes them (#597). The page asks for the kinds,
    /// shows one Save dialog that offers each as a file type, and has the
    /// kind the user picked written where the user said.
    /// </summary>
    internal interface IAutomationLibraries
    {
        /// <summary>The libraries in the order the dialog lists them. The
        /// first is the one the dialog starts on.</summary>
        IReadOnlyList<AutomationLibraryKind> Kinds { get; }

        /// <summary>Writes the library of <paramref name="kind"/> to
        /// <paramref name="path"/>, replacing a file that is there. A failure
        /// throws.</summary>
        void Save(AutomationLibraryKind kind, string path);
    }
}
