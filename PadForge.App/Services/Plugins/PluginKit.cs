using System;
using System.Collections.Generic;
using System.IO;
using PadForge.Services.Automation;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// The plugin kit embedded in PadForge (#597, #600 item 20). Save the
    /// Plugin Kit… and <c>--plugin-kit &lt;folder&gt;</c> write it: a guide
    /// to every verb, event, declaration and kind of setting (README.md), a
    /// complete small plugin in C# with its build settings file and build
    /// script, and the same plugin in each library's language with the
    /// library beside it. Each file opens with how to build or run it and
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
            ("README.md", "PadForge.Plugins.README.md"),
            ("PadForgePlugin.cs", "PadForge.Plugins.PadForgePlugin.cs"),
            ("PipeOwner.cs", "PadForge.Plugins.PipeOwner.cs"),
            ("PadForgePlugin.csproj", "PadForge.Plugins.PadForgePlugin.csproj"),
            ("build.ps1", "PadForge.Plugins.build.ps1"),
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
            WriteInto(folder);
            return folder;
        }

        /// <summary>
        /// Writes the kit's files into <paramref name="folder"/>, made when it
        /// is not there, as <c>--plugin-kit</c> does: the folder the user
        /// named on the command line is the kit's. A kit file of the same
        /// name there is written over, and nothing else is touched.
        /// </summary>
        internal static void WriteInto(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("No folder was named.", nameof(folder));
            Directory.CreateDirectory(folder);
            foreach (var (name, _) in Files) File.WriteAllBytes(Path.Combine(folder, name), Read(name));
            // Each script plugin imports its library from its own folder.
            foreach (var library in AutomationLibraries.All)
                File.WriteAllBytes(Path.Combine(folder, library.FileName), AutomationLibraries.Read(library.Kind));
        }

        /// <summary>
        /// <c>--plugin-kit &lt;folder&gt;</c>: writes the kit, says where and
        /// how to build and add a plugin, and gives 0, or names what failed
        /// and gives 1. It runs in the copy the command line started, with
        /// the user's rights, and needs no running PadForge.
        /// </summary>
        internal static int RunFromCommandLine(string folder, Action<string> say, Action<string> sayError)
        {
            string full;
            try { full = Path.GetFullPath(folder); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                sayError?.Invoke("The plugin kit could not be written to " + folder + ": " + e.Message);
                return 1;
            }
            try { WriteInto(full); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                sayError?.Invoke("The plugin kit could not be written to " + full + ": " + e.Message);
                return 1;
            }
            say?.Invoke("The plugin kit is written to " + full + ".\r\n"
                        + "README.md there lists every verb, event and kind of setting.\r\n"
                        + "To build the C# plugin: powershell -ExecutionPolicy Bypass -File \"" + Path.Combine(full, "build.ps1") + "\"\r\n"
                        + "To add a plugin, open PadForge, go to Extras, Plugins, press Add a Plugin... and pick its file.");
            return 0;
        }
    }
}
