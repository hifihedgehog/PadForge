using System;
using System.IO;
using PadForge.Engine.Recording;

namespace PadForge.Common.Recording
{
    /// <summary>
    /// <c>PadForge.exe --summarize &lt;file&gt;</c> (#600): prints a
    /// recording's summary, the one its review shows first and Copy Summary
    /// hands over, from a finished zip or from the text of one that still
    /// runs. A file that has no summary yet has one worked out from its
    /// events. The file is read within <see cref="BoundedLineReader"/>'s
    /// limits, since it can come from anyone.
    /// </summary>
    internal static class RecordingSummaryPrint
    {
        /// <summary>Exit codes: 0 printed, 1 the file could not be read, 2
        /// it is no recording.</summary>
        internal static int Run(string path, Action<string> say, Action<string> sayError)
        {
            try
            {
                using var reader = new RecordingReader(new BoundedLineReader(RecordingReader.OpenText(path)));
                if (!reader.IsRecordingFile)
                {
                    sayError?.Invoke(path + " is no PadForge recording.");
                    return 2;
                }
                var lines = reader.Summary.Count > 0 ? reader.Summary : RecordingSummary.Read(reader).Lines();
                say?.Invoke(string.Join(Environment.NewLine, lines));
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or ArgumentException or NotSupportedException)
            {
                sayError?.Invoke("PadForge could not read " + path + ": " + ex.Message);
                return 1;
            }
        }
    }
}
