using System;
using System.Collections.Generic;
using System.IO;
using PadForge.Resources.Strings;

namespace PadForge.Services.Automation
{
    /// <summary>A library PadForge carries for scripts (#597).</summary>
    internal enum AutomationLibraryKind
    {
        AutoHotkey,
        Python,
        PowerShell,
    }

    /// <summary>One library: its kind, the file name it is saved under, and
    /// its name and file filter in the language in use.</summary>
    internal sealed record AutomationLibrary(AutomationLibraryKind Kind, string FileName, string ResourceName)
    {
        /// <summary>The library's name, such as AutoHotkey v2.</summary>
        public string Name => Kind switch
        {
            AutomationLibraryKind.Python => Strings.Instance.Automation_Library_Python,
            AutomationLibraryKind.PowerShell => Strings.Instance.Automation_Library_PowerShell,
            _ => Strings.Instance.Automation_Library_AutoHotkey,
        };

        /// <summary>The save dialog's filter for the file.</summary>
        public string Filter => Kind switch
        {
            AutomationLibraryKind.Python => Strings.Instance.Automation_Library_Python_Filter,
            AutomationLibraryKind.PowerShell => Strings.Instance.Automation_Library_PowerShell_Filter,
            _ => Strings.Instance.Automation_Library_AutoHotkey_Filter,
        };
    }

    /// <summary>
    /// The script libraries embedded in PadForge (#597): AutoHotkey v2,
    /// Python and PowerShell, each one file that speaks protocol 1 of the
    /// Automation pipe. The Automation page's Save a Library button lists
    /// <see cref="All"/> and writes the one the user picks with
    /// <see cref="Save"/> where the user says. PadForge writes no library by
    /// itself. <see cref="Instance"/> is what the window hands the host as
    /// its <see cref="AutomationHost.Libraries"/>.
    /// </summary>
    internal sealed class AutomationLibraries : IAutomationLibraries
    {
        /// <summary>The libraries as the host and the Automation page reach
        /// them.</summary>
        internal static AutomationLibraries Instance { get; } = new AutomationLibraries();

        private AutomationLibraries() { }

        IReadOnlyList<AutomationLibrary> IAutomationLibraries.All => All;

        void IAutomationLibraries.Save(AutomationLibraryKind kind, string path) => Save(kind, path);

        /// <summary>Every library, in the order a picker lists them.</summary>
        internal static IReadOnlyList<AutomationLibrary> All { get; } = new[]
        {
            new AutomationLibrary(AutomationLibraryKind.AutoHotkey, "PadForge.ahk", "PadForge.Automation.PadForge.ahk"),
            new AutomationLibrary(AutomationLibraryKind.Python, "padforge.py", "PadForge.Automation.padforge.py"),
            new AutomationLibrary(AutomationLibraryKind.PowerShell, "PadForge.psm1", "PadForge.Automation.PadForge.psm1"),
        };

        internal static AutomationLibrary Of(AutomationLibraryKind kind)
        {
            foreach (var library in All)
                if (library.Kind == kind) return library;
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        /// <summary>The library's bytes as PadForge carries them.</summary>
        internal static byte[] Read(AutomationLibraryKind kind)
        {
            var library = Of(kind);
            using var stream = typeof(AutomationLibraries).Assembly.GetManifestResourceStream(library.ResourceName)
                ?? throw new InvalidOperationException("PadForge carries no " + library.FileName + ".");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        /// <summary>Writes the library to <paramref name="path"/>, which the
        /// user picked, replacing a file there. Throws the file system's
        /// error for a path that cannot be written.</summary>
        internal static void Save(AutomationLibraryKind kind, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("No file was named.", nameof(path));
            File.WriteAllBytes(path, Read(kind));
        }
    }
}
