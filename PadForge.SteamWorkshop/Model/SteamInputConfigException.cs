using System;

namespace PadForge.SteamWorkshop.Model
{
    /// <summary>
    /// Thrown when a parsed VDF document is not a usable Steam Input configuration:
    /// missing <c>controller_mappings</c>, missing / non-numeric version, or a version
    /// older than the supported schema (3).
    /// </summary>
    public sealed class SteamInputConfigException : Exception
    {
        public SteamInputConfigException(string message) : base(message) { }
    }
}
