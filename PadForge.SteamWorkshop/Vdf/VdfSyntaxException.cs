using System;

namespace PadForge.SteamWorkshop.Vdf
{
    /// <summary>
    /// Thrown when a Steam KeyValues (VDF) document is malformed, oversized, binary,
    /// or exceeds the nesting-depth cap. Carries the byte offset and line/column of the
    /// failure so callers can surface a precise diagnostic.
    /// </summary>
    public sealed class VdfSyntaxException : Exception
    {
        /// <summary>Zero-based character offset into the input where parsing failed.</summary>
        public int Offset { get; }

        /// <summary>One-based line number of the failure.</summary>
        public int Line { get; }

        /// <summary>One-based column number of the failure.</summary>
        public int Column { get; }

        public VdfSyntaxException(string message, int offset, int line, int column)
            : base($"{message} (line {line}, column {column})")
        {
            Offset = offset;
            Line = line;
            Column = column;
        }
    }
}
