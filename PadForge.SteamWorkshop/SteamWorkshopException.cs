using System;

namespace PadForge.SteamWorkshop
{
    /// <summary>
    /// Raised when a Steam network operation fails in a way the caller should surface:
    /// connect / logon timeouts, non-OK Steam results, oversized or non-VDF downloads.
    /// </summary>
    public sealed class SteamWorkshopException : Exception
    {
        public SteamWorkshopException(string message) : base(message) { }

        public SteamWorkshopException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
