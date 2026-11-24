using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PadForge.Services.Install
{
    /// <summary>
    /// What an install writes (#600), and nothing else: a folder of its own
    /// brought to exactly the files given, a file of its own, or its own
    /// entry inside another program's settings file.
    ///
    /// <para>A file whose bytes already match is left with its time. A file
    /// that differs is written beside itself and moved over the old one, so
    /// a reader never sees half of it. A file an older copy had and this one
    /// does not is removed, with any folder that leaves empty. A folder is
    /// PadForge's own only when its last part is named for it.</para>
    /// </summary>
    internal static class OwnFiles
    {
        /// <summary>The names a folder of PadForge's own carries.</summary>
        internal static readonly string[] OwnNames = { "padforge", "PadForge" };

        /// <summary>Whether a folder is PadForge's own by its name.</summary>
        internal static bool IsOwnFolder(string folder)
            => OwnNames.Contains(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder ?? string.Empty)), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Brings <paramref name="folder"/> to exactly <paramref name="files"/>
        /// (paths relative to it, with / between parts). Refuses a folder that
        /// is not PadForge's own by name. Gives how many files it wrote and
        /// removed.
        /// </summary>
        internal static (int Written, int Removed) Mirror(string folder, IReadOnlyList<(string Path, byte[] Bytes)> files)
        {
            if (!IsOwnFolder(folder)) throw new InvalidOperationException("PadForge writes only a folder of its own, and " + folder + " is not.");
            if (files.Count == 0 || !IsPriorInstall(folder, files[0].Path))
                throw new InvalidOperationException(folder + " holds files PadForge did not write, so PadForge leaves it as it is.");
            string root = Path.GetFullPath(folder);
            Directory.CreateDirectory(root);
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int written = 0, removed = 0;
            foreach (var (relative, bytes) in files)
            {
                string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A file of the install would land outside its folder: " + relative);
                kept.Add(path);
                if (WriteIfDifferent(path, bytes)) written++;
            }
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
            {
                if (kept.Contains(path)) continue;
                File.Delete(path);
                removed++;
            }
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            return (written, removed);
        }

        /// <summary>Whether <paramref name="folder"/> holds exactly
        /// <paramref name="files"/>, byte for byte, and nothing more. Null when
        /// it does not exist.</summary>
        internal static bool? Matches(string folder, IReadOnlyList<(string Path, byte[] Bytes)> files)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            string root = Path.GetFullPath(folder);
            int count = 0;
            foreach (var (relative, bytes) in files)
            {
                string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return false;
                count++;
            }
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() == count;
        }

        /// <summary>
        /// Whether a folder is empty, missing, or an install PadForge wrote:
        /// it holds <paramref name="marker"/>, the install's first file, and
        /// a skill's SKILL.md names padforge. A folder of the same name the
        /// user keeps for something else is never written over or removed,
        /// whatever its case.
        /// </summary>
        internal static bool IsPriorInstall(string folder, string marker)
        {
            if (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any()) return true;
            string path = Path.Combine(folder, marker.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return false;
            if (!string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string line in File.ReadLines(path).Take(20))
                if (line.Trim().Equals("name: padforge", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Removes a folder of PadForge's own and everything in it,
        /// when it is an install of <paramref name="marker"/>'s. True when
        /// there was one.</summary>
        internal static bool RemoveFolder(string folder, string marker)
        {
            if (!IsOwnFolder(folder)) throw new InvalidOperationException("PadForge removes only a folder of its own, and " + folder + " is not.");
            if (!Directory.Exists(folder)) return false;
            if (!IsPriorInstall(folder, marker))
                throw new InvalidOperationException(folder + " holds files PadForge did not write, so PadForge leaves it as it is.");
            Directory.Delete(folder, recursive: true);
            return true;
        }

        /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/>
        /// unless the file holds them already. True when it wrote.</summary>
        internal static bool WriteIfDifferent(string path, byte[] bytes)
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".padforge-new";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            return true;
        }

        // ── An entry in another program's settings file ──

        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        /// <summary>
        /// Reads a JSON settings file as an object, or an empty one when it
        /// is not there. Comments and trailing commas are read, as the hosts
        /// that allow them write them. Throws for a file that is not JSON,
        /// so a file PadForge cannot read is never written over.
        /// </summary>
        internal static JsonObject ReadJson(string path)
        {
            if (!File.Exists(path)) return new JsonObject();
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
            JsonNode node;
            try
            {
                node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                    { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(path + " is not JSON PadForge can read, so PadForge leaves it as it is: " + ex.Message, ex);
            }
            return node as JsonObject ?? throw new InvalidOperationException(path + " does not hold a JSON object, so PadForge leaves it as it is.");
        }

        /// <summary>Sets <paramref name="name"/> under
        /// <paramref name="container"/> to <paramref name="entry"/> in the
        /// file's own text (JsoncEdit), so every other key, comment and line
        /// stays as the user wrote it.</summary>
        internal static void SetJsonEntry(string path, string container, string name, JsonObject entry)
        {
            var root = ReadJson(path);
            if (root[container] != null && root[container] is not JsonObject)
                throw new InvalidOperationException(path + " has a " + container + " that is not an object, so PadForge leaves it as it is.");
            var (text, bom) = ReadText(path);
            WriteText(path, JsoncEdit.Set(text, container, name, entry.ToJsonString(Indented)), bom);
        }

        /// <summary>Takes <paramref name="name"/> out from under
        /// <paramref name="container"/>, in the file's own text. True when it
        /// was there.</summary>
        internal static bool RemoveJsonEntry(string path, string container, string name)
        {
            if (!File.Exists(path)) return false;
            if (ReadJson(path)[container]?[name] == null) return false;
            var (text, bom) = ReadText(path);
            string edited = JsoncEdit.Remove(text, container, name);
            if (edited == null) return false;
            WriteText(path, edited, bom);
            return true;
        }

        /// <summary>The entry under <paramref name="container"/>, or null.
        /// Null too for a file PadForge cannot read.</summary>
        internal static JsonObject JsonEntry(string path, string container, string name)
        {
            try { return ReadJson(path)[container]?[name] as JsonObject; }
            catch (InvalidOperationException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static (string Text, bool Bom) ReadText(string path)
        {
            if (!File.Exists(path)) return (string.Empty, false);
            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return (new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
        }

        private static void WriteText(string path, string text, bool bom)
        {
            byte[] body = new UTF8Encoding(false).GetBytes(text);
            WriteIfDifferent(path, bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body);
        }
    }
}
