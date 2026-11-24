using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Common.Input
{
    /// <summary>Why a downloaded file was refused whole.</summary>
    internal enum ControllerDatabaseRefusal
    {
        None,
        /// <summary>Over 4 MB. The community file is about 0.6 MB.</summary>
        TooLarge,
        /// <summary>Under 100 KB.</summary>
        TooSmall,
        /// <summary>Not UTF-8 text, or text with a NUL or another control
        /// character that is no line end and no tab.</summary>
        NotText,
        /// <summary>Fewer than four in five of the controllers this release
        /// already knows have a row in it.</summary>
        TooFewKnownRows,
    }

    /// <summary>
    /// A downloaded copy of the community controller database (#597), read
    /// and checked. No publisher digest exists for the file, so what stands
    /// between the network and SDL is HTTPS to a fixed host, the caps, the
    /// row grammar (<see cref="ControllerDatabaseRow"/>) and the count of
    /// rows this release already knows.
    /// </summary>
    internal sealed class ControllerDatabaseDownload
    {
        internal const int MaxBytes = 4 * 1024 * 1024;
        internal const int MinBytes = 100 * 1024;

        public ControllerDatabaseRefusal Refusal { get; private init; }

        /// <summary>The accepted rows, in the file's order. Empty when the
        /// file was refused.</summary>
        public IReadOnlyList<ControllerDatabaseRow> Rows { get; private init; } = Array.Empty<ControllerDatabaseRow>();

        /// <summary>Lines of the file that carry the Windows platform field.</summary>
        public int WindowsRows { get; private init; }

        /// <summary>Lowercase SHA-256 of the bytes read.</summary>
        public string Sha256 { get; private init; }

        /// <summary>Of the controller IDs this release has a pinned row for,
        /// how many have an accepted row in the file.</summary>
        public int KnownRows { get; private init; }

        public static ControllerDatabaseDownload Read(byte[] bytes, ControllerDatabaseBaseline baseline)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (bytes.Length > MaxBytes) return Refuse(ControllerDatabaseRefusal.TooLarge, sha256);
            if (bytes.Length < MinBytes) return Refuse(ControllerDatabaseRefusal.TooSmall, sha256);

            string text;
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (ArgumentException)
            {
                return Refuse(ControllerDatabaseRefusal.NotText, sha256);
            }
            foreach (char c in text)
                if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
                    return Refuse(ControllerDatabaseRefusal.NotText, sha256);

            var byId = new Dictionary<string, ControllerDatabaseRow>(StringComparer.Ordinal);
            var repeated = new HashSet<string>(StringComparer.Ordinal);
            var order = new List<string>();
            int windowsRows = 0;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Length > 0 && raw[^1] == '\r' ? raw.Substring(0, raw.Length - 1) : raw;
                if (line.Length == 0 || line[0] == '#' || !line.Contains(ControllerDatabaseRow.WindowsField, StringComparison.Ordinal))
                    continue;
                windowsRows++;
                if (!ControllerDatabaseRow.TryParse(line, out var row)) continue;
                // Two rows for one controller would leave SDL to keep the
                // later one. Neither is taken.
                if (repeated.Contains(row.Guid)) continue;
                if (byId.Remove(row.Guid))
                {
                    repeated.Add(row.Guid);
                    continue;
                }
                byId.Add(row.Guid, row);
                order.Add(row.Guid);
            }

            var rows = new List<ControllerDatabaseRow>(byId.Count);
            foreach (string id in order)
                if (byId.TryGetValue(id, out var row)) rows.Add(row);

            int known = 0;
            foreach (string id in baseline.PinnedIds)
                if (byId.ContainsKey(id)) known++;
            // Four in five, in whole numbers.
            var refusal = known * 5L < baseline.PinnedIds.Count * 4L
                ? ControllerDatabaseRefusal.TooFewKnownRows
                : ControllerDatabaseRefusal.None;
            return new ControllerDatabaseDownload
            {
                Refusal = refusal,
                Rows = refusal == ControllerDatabaseRefusal.None ? rows : Array.Empty<ControllerDatabaseRow>(),
                WindowsRows = windowsRows,
                Sha256 = sha256,
                KnownRows = known,
            };
        }

        private static ControllerDatabaseDownload Refuse(ControllerDatabaseRefusal why, string sha256) =>
            new() { Refusal = why, Sha256 = sha256 };
    }
}
