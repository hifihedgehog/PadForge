using System;
using System.Collections.Generic;
using PadForge.Services.Automation;

namespace PadForge.ViewModels
{
    /// <summary>
    /// A program the user allowed to work with PadForge at Read or above
    /// (#600 item 5), as the Automation page's Allowed Programs card lists
    /// it: the consent store's record put in the page's terms.
    /// </summary>
    /// <param name="Key">The store's own key for the program, which Remove
    /// takes.</param>
    /// <param name="Name">The name to show: the program's own, or an
    /// assistant program's name for one added on its row.</param>
    /// <param name="Who">The program's image path and signer, and for a
    /// script its interpreter and script file, as one line.</param>
    /// <param name="Level">The level it asked for and was allowed.</param>
    /// <param name="LastUsed">When it last connected, or null for never.</param>
    /// <param name="ConnectedNow">Whether it is connected now.</param>
    internal sealed record AllowedProgramView(string Key, string Name, string Who, AutomationLevel Level,
        DateTimeOffset? LastUsed, bool ConnectedNow);

    /// <summary>
    /// The consent store the Automation page shows and changes (#600 items 5
    /// and 9): the programs allowed, Remove for each, and the Add on an
    /// assistant's row, which is the user's consent for that assistant. The
    /// store keeps its list in PadForge.xml. The page reads it on the
    /// interface's thread when <see cref="Changed"/> is raised, from any
    /// thread.
    /// </summary>
    internal interface IAutomationConsent
    {
        /// <summary>The programs allowed now.</summary>
        IReadOnlyList<AllowedProgramView> Allowed { get; }

        /// <summary>Raised after the list changes, on any thread.</summary>
        event EventHandler Changed;

        /// <summary>Takes a program off the list. It asks again the next
        /// time it connects at Read or above.</summary>
        void Remove(string key);

        /// <summary>The user added an assistant program on its row: its copy
        /// of PadForge.exe --mcp is allowed at the level it asks for, up to
        /// the page's level, without the question card.</summary>
        /// <param name="hostKey">The program's key in AssistantHosts, such as
        /// claude-code.</param>
        /// <param name="hostName">Its name, as its row shows it.</param>
        /// <param name="program">The program's own file, or null.</param>
        void AllowAssistant(string hostKey, string hostName, string program);

        /// <summary>The user removed an assistant program on its row: its
        /// consent goes with it.</summary>
        /// <param name="hostKey">The program's key in AssistantHosts.</param>
        /// <param name="program">The program's own file, as Add gave it.</param>
        void ForgetAssistant(string hostKey, string program);
    }
}
