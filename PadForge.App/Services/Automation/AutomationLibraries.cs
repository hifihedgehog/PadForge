using System;
using System.Collections.Generic;
using System.IO;
using PadForge.Resources.Strings;

namespace PadForge.Services.Automation
{
    /// <summary>A library PadForge carries for scripts (#600).</summary>
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
    /// The script libraries embedded in PadForge (#600): AutoHotkey v2,
    /// Python and PowerShell, each one file that speaks protocol 1 of the
    /// Automation pipe. The Scripts card (#600 item 26) installs each where
    /// its language finds it, and its Save a Library… lists
    /// <see cref="All"/> and has the one the user picks written where the
    /// user says, both by <c>PadForge.exe --install</c> run with the user's
    /// own rights. PadForge writes no library by itself.
    /// </summary>
    internal sealed class AutomationLibraries : IAutomationLibraries
    {
        /// <summary>The libraries as the Automation page's Save a Library
        /// reaches them.</summary>
        internal static AutomationLibraries Instance { get; } = new AutomationLibraries();

        private AutomationLibraries() { }

        IReadOnlyList<AutomationLibrary> IAutomationLibraries.All => All;

        /// <summary>The page's Save a Library: <c>--install</c> started with
        /// the user's own rights (#600), so the file is the user's.</summary>
        void IAutomationLibraries.Save(AutomationLibraryKind kind, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("No file was named.", nameof(path));
            var (code, error) = Install.UserInstall.InCopyAsync(new Install.InstallRequest(WhatOf(kind), path)).GetAwaiter().GetResult();
            if (code != Install.UserInstall.Done) throw new InvalidOperationException(error ?? "The install ended with code " + code + ".");
        }

        internal static Install.InstallWhat WhatOf(AutomationLibraryKind kind) => kind switch
        {
            AutomationLibraryKind.Python => Install.InstallWhat.Python,
            AutomationLibraryKind.PowerShell => Install.InstallWhat.PowerShell,
            _ => Install.InstallWhat.AutoHotkey,
        };

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
    }
}
