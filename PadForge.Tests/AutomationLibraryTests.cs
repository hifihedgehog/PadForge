using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A fact that runs only where its interpreter is on this PC,
    /// and says which one is missing when it is not.</summary>
    public sealed class InterpreterFactAttribute : FactAttribute
    {
        public InterpreterFactAttribute(string interpreter)
        {
            if (AutomationLibraryTests.Interpreter(interpreter) == null)
                Skip = interpreter + " is not on this PC, so its library is not run against a host.";
        }
    }

    /// <summary>
    /// The script libraries PadForge carries (#597): each is saved where the
    /// user says, opens with what it is, and works against a real host on a
    /// pipe name of the test's own. The scripts under test send no key and
    /// move no pointer. The AutoHotkey script sets OnError so an error writes
    /// its line and exits instead of leaving a dialog open, and every process
    /// a test starts is ended by it.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationLibraryTests
    {
        internal const string AutoHotkeyExe = @"C:\tmp\padforge6-build\auto-probe\ahk\v2\AutoHotkey64.exe";

        /// <summary>The interpreter's path, or null when this PC has
        /// none.</summary>
        internal static string Interpreter(string which)
        {
            switch (which)
            {
                case "autohotkey":
                    return File.Exists(AutoHotkeyExe) ? AutoHotkeyExe : null;
                case "powershell":
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe");
                    return File.Exists(path) ? path : null;
                }
                default:
                    return FindPython();
            }
        }

        private static string _python;

        private static string FindPython()
        {
            if (_python != null) return _python.Length == 0 ? null : _python;
            var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';')
                .Where(dir => dir.Length > 0 && !dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                .Select(dir => Path.Combine(dir.Trim(), "python.exe"))
                .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Python\bin\python.exe"));
            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(path, "-I -c \"import sys,_winapi;print(sys.version_info[0])\"")
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    if (text.Trim() == "3") return _python = path;
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
            _python = string.Empty;
            return null;
        }

        private static string NewFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Libraries." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ── Carried and saved ──

        [Fact]
        public void EveryLibrary_IsCarried_AndSavedWhereTheUserSays()
        {
            Assert.Equal(new[] { "PadForge.ahk", "padforge.py", "PadForge.psm1" }, AutomationLibraries.All.Select(l => l.FileName));
            string dir = NewFolder();
            try
            {
                foreach (var library in AutomationLibraries.All)
                {
                    Assert.False(string.IsNullOrWhiteSpace(library.Name));
                    Assert.EndsWith("|*" + Path.GetExtension(library.FileName), library.Filter);
                    string path = Path.Combine(dir, library.FileName);
                    File.WriteAllText(path, "old");
                    AutomationLibraries.Save(library.Kind, path);
                    byte[] saved = File.ReadAllBytes(path);
                    Assert.Equal(AutomationLibraries.Read(library.Kind), saved);
                    string text = Encoding.UTF8.GetString(saved);
                    // Each opens with what it is, the protocol it speaks and
                    // one example.
                    string head = text.Substring(0, Math.Min(text.Length, 1200));
                    Assert.Contains("PadForge Automation for", head);
                    Assert.Contains("protocol 1", head);
                    Assert.Contains("Example", head);
                    Assert.DoesNotContain("\t", text);
                }
                Assert.Throws<ArgumentException>(() => AutomationLibraries.Save(AutomationLibraryKind.Python, " "));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void EveryLibrary_SaysWhichItIs_WhenPadForgeIsClosedOffOrTooLow()
        {
            foreach (var library in AutomationLibraries.All)
            {
                string text = Encoding.UTF8.GetString(AutomationLibraries.Read(library.Kind));
                Assert.Contains("PadForge is not running.", text);
                Assert.Contains("PadForge is running, but Automation is off. Turn it on in Extras, Automation.", text);
                Assert.Contains("too low for it. Raise it in Extras, Automation.", text);
                Assert.Contains("PadForge.Instance", text);
            }
        }

        /// <summary>Each library's head names the button that installs it as
        /// the Scripts card shows it: Install, on the library's own row, on
        /// the Extras page's Automation page (#600 item 26). The card has no
        /// button that reads "Install for".</summary>
        [Fact]
        public void EveryLibrarysHead_NamesInstallOnItsRowOfTheScriptsCard()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            foreach (var library in AutomationLibraries.All)
            {
                string text = Encoding.UTF8.GetString(AutomationLibraries.Read(library.Kind));
                // The head as prose: comment marks gone, one space between
                // words.
                string head = System.Text.RegularExpressions.Regex.Replace(
                    System.Text.RegularExpressions.Regex.Replace(text.Substring(0, Math.Min(text.Length, 1600)), @"(?m)^;[ ]?", string.Empty),
                    @"\s+", " ");
                Assert.Equal(library.Name, library.Kind switch
                {
                    AutomationLibraryKind.AutoHotkey => s.Automation_Library_AutoHotkey,
                    AutomationLibraryKind.Python => s.Automation_Library_Python,
                    _ => s.Automation_Library_PowerShell,
                });
                Assert.Contains("Install it from PadForge with " + s.Common_Install + " on the " + library.Name + " row of the "
                    + s.Extras_Automation_Card_Scripts + " card (" + s.Extras_Title + ", " + s.Extras_Automation_Title + ")", head);
                Assert.DoesNotContain("Install for", text);
            }
        }

        // Each library's runs against a real host are in ScriptAutoHotkeyTests,
        // ScriptPythonTests and ScriptPowerShellTests (#600 item 27).

        private static void TryDelete(string dir)
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    Directory.Delete(dir, true);
                    return;
                }
                catch (IOException) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { Thread.Sleep(100); }
            }
        }
    }
}
