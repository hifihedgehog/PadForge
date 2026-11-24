namespace PadForge.Services.Automation
{
    /// <summary>
    /// What <c>PadForge.exe --mcp</c> runs (#597): the copy an assistant
    /// starts, which speaks to the assistant over standard input and output
    /// and to the running PadForge through the door.
    ///
    /// <para>This build carries the switch and not the connection behind it.
    /// The run says so on standard error, where an assistant's host shows a
    /// server's own words, and writes nothing to standard output, which
    /// carries the protocol alone.</para>
    /// </summary>
    internal static class AssistantBridge
    {
        internal const string NotInThisBuild = "The assistant connection is not in this build of PadForge.";

        /// <summary>The run's exit code.</summary>
        internal const int NotAvailable = 1;

        /// <summary>The whole run. Returns the process's exit code.</summary>
        internal static int Run()
        {
            CallerConsole.SayError(NotInThisBuild);
            return NotAvailable;
        }
    }
}
