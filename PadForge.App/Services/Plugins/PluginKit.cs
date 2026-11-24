using System;
using System.Collections.Generic;
using System.IO;
using PadForge.Services.Automation;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// The plugin kit embedded in PadForge (#597). Save the Plugin Kit
    /// writes it into a new folder where the user says: a complete small
    /// plugin in C#, the same plugin in each library's language with the
    /// library beside it, each file opening with how to build or run it and
    /// how to add it on the Plugins page. PadForge writes no kit by
    /// itself.
    /// </summary>
    internal static class PluginKit
    {
        internal const string FolderName = "PadForge Plugin Kit";

        /// <summary>The kit's own files, by the name each is saved under and
        /// its resource.</summary>
        internal static IReadOnlyList<(string FileName, string Resource)> Files { get; } = new[]
        {
            ("PadForgePlugin.cs", "PadForge.Plugins.PadForgePlugin.cs"),
            ("plugin.py", "PadForge.Plugins.plugin.py"),
            ("plugin.ps1", "PadForge.Plugins.plugin.ps1"),
            ("plugin.ahk", "PadForge.Plugins.plugin.ahk"),
        };

        /// <summary>A kit file's bytes as PadForge carries them.</summary>
        internal static byte[] Read(string fileName)
        {
            foreach (var (name, resource) in Files)
            {
                if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) continue;
                using var stream = typeof(PluginKit).Assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException("PadForge carries no " + name + ".");
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            }
            throw new ArgumentOutOfRangeException(nameof(fileName));
        }

        /// <summary>
        /// Writes the kit into a new folder in <paramref name="parent"/>,
        /// which the user picked: <see cref="FolderName"/>, or that name and
        /// a number when a folder of that name is there already, so nothing
        /// of the user's is written over. Gives the folder. Throws the file
        /// system's error for a folder that cannot be written.
        /// </summary>
        internal static string Save(string parent)
        {
            if (string.IsNullOrWhiteSpace(parent)) throw new ArgumentException("No folder was named.", nameof(parent));
            string folder = Path.Combine(parent, FolderName);
            for (int n = 2; Directory.Exists(folder) || File.Exists(folder); n++)
                folder = Path.Combine(parent, FolderName + " " + n);
            Directory.CreateDirectory(folder);
            foreach (var (name, _) in Files) File.WriteAllBytes(Path.Combine(folder, name), Read(name));
            // Each script plugin imports its library from its own folder.
            foreach (var library in AutomationLibraries.All)
                File.WriteAllBytes(Path.Combine(folder, library.FileName), AutomationLibraries.Read(library.Kind));
            return folder;
        }
    }
}
