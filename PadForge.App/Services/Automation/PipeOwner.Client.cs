using System.IO.Pipes;
using System.Security.Principal;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The app's own part of <see cref="PipeOwner"/>, which the plugin kit
    /// does not carry: the client end PadForge's copies open, and the test
    /// fence on it (#597).
    /// </summary>
    internal static partial class PipeOwner
    {
        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests PipeOwnerOffLimits), never by the app. While it is
        /// set, no client end of a pipe of PadForge's own names is opened, so
        /// a test never reaches a PadForge that runs on the same PC. A pipe of
        /// a test's own name opens as before.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>A client end of the pipe, opened so the server can
        /// identify the client and never act as it. Null under the test
        /// fence for a pipe of PadForge's own names.</summary>
        internal static NamedPipeClientStream Open(string pipeName)
        {
            if (OffLimitsForTest && IsOwnName(pipeName)) return null;
            return new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        }
    }
}
