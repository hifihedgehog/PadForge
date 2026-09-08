using System;
using System.Collections.Generic;
using System.Text;

namespace PadForge.Services
{
    internal readonly record struct PsmRadioState(int Index, bool Enabled, string Name);

    internal readonly record struct PsmQueryResult(bool Success, int BytesReturned, int Error);

    /// <summary>A complete filter census or the precise point where inspection failed.</summary>
    internal sealed class PsmPatchSnapshot
    {
        internal const int BufferSize = 408;
        internal const int MaximumRadios = 32;
        internal const int NoSuchDevice = 433;
        internal const int InvalidData = 13;

        private PsmPatchSnapshot(bool complete, PsmRadioState[] radios, int error, bool controlPresent = true)
        {
            Complete = complete;
            ControlPresent = controlPresent;
            Radios = radios;
            Error = error;
            foreach (var radio in radios)
                if (radio.Enabled) ArmedCount++;
        }

        internal bool Complete { get; }
        internal bool ControlPresent { get; }
        internal IReadOnlyList<PsmRadioState> Radios { get; }
        internal int ArmedCount { get; }
        internal int Error { get; }

        internal static PsmPatchSnapshot Unavailable(int error) => new(false, Array.Empty<PsmRadioState>(), error, false);

        /// <summary>
        /// BthPS3.h:435 defines the packed response. Sideband.c:473 ends the
        /// census with STATUS_NO_SUCH_DEVICE and :517 returns all 408 bytes.
        /// A failed query or a truncated response cannot establish filter state.
        /// </summary>
        internal static PsmPatchSnapshot Read(Func<byte[], PsmQueryResult> query)
        {
            var radios = new List<PsmRadioState>();
            for (int index = 0; index <= MaximumRadios; index++)
            {
                byte[] buffer = new byte[BufferSize];
                BitConverter.GetBytes(index).CopyTo(buffer, 0);
                PsmQueryResult result = query(buffer);
                if (!result.Success)
                    return new(result.Error == NoSuchDevice, radios.ToArray(),
                        result.Error == NoSuchDevice ? 0 : result.Error);

                if (index == MaximumRadios || result.BytesReturned != BufferSize
                    || BitConverter.ToInt32(buffer, 0) != index)
                    return new(false, radios.ToArray(), InvalidData);

                // Some driver builds omit long symbolic names. The enabled
                // value remains usable, so a name is optional metadata.
                string name = Encoding.Unicode.GetString(buffer, 8, BufferSize - 8);
                int terminator = name.IndexOf('\0');
                if (terminator >= 0) name = name.Substring(0, terminator);
                radios.Add(new(index, BitConverter.ToUInt32(buffer, 4) != 0, name));
            }
            return new(false, radios.ToArray(), InvalidData);
        }
    }
}
