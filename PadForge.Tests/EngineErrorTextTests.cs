using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The engine's failures reach the message line in the interface's
    /// language. RaiseError's status text sat on the message line inside
    /// "Error: {0}", and about fifty calls passed an English sentence as that
    /// text, a zero-based pad index among them ("Error publishing the steering
    /// angle for pad 0"), so a German interface read "Fehler: Error opening
    /// mouse (...)". Each call now passes a Strings line and keeps its English
    /// sentence for the diagnostics log.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class EngineErrorTextTests
    {
        private static readonly Regex EnglishStatus = new(
            @"\bRaiseError(Resolved)?\(\s*\$?""", RegexOptions.Compiled);

        [Fact]
        public void NoEngineErrorPassesAnEnglishSentenceAsItsStatusText()
        {
            var found = new List<string>();
            foreach (var file in Directory.EnumerateFiles(Path.Combine(FindRoot(), "PadForge.App", "Common", "Input"), "*.cs", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    if (EnglishStatus.IsMatch(lines[i]))
                        found.Add(Path.GetFileName(file) + ":" + (i + 1));
            }
            Assert.Empty(found);
        }

        [Fact]
        public void TheTwoArgumentRaiseErrorIsGone()
        {
            var overload = typeof(InputManager).GetMethod("RaiseError",
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(Exception) }, null);
            Assert.Null(overload);
        }

        [Theory]
        [InlineData("RaiseDeviceError", "Status_DeviceFailed")]
        [InlineData("RaiseEngineError", "Status_EngineFailed")]
        public void AFailureShowsItsStringsLineAndLogsItsSentence(string raise, string key)
        {
            var im = new InputManager();
            string shown = null;
            im.ErrorOccurred += (_, e) => shown = e.Message;
            var method = typeof(InputManager).GetMethod(raise, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(im, new object[] { "Error opening mouse (Probe)", new InvalidOperationException("probe") });
            string expected = (string)typeof(Strings).GetProperty(key)?.GetValue(Strings.Instance);
            Assert.False(string.IsNullOrEmpty(expected));
            Assert.Equal(expected, shown);
        }

        [Fact]
        public void ASlotFailureNamesTheSlotAsTheInterfaceDoes()
        {
            var im = new InputManager();
            string shown = null;
            im.ErrorOccurred += (_, e) => shown = e.Message;
            var method = typeof(InputManager).GetMethod("RaiseSlotError", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(im, new object[] { 0, "Error combining states for pad 0", new InvalidOperationException("probe") });
            string label = (string)typeof(InputManager).GetMethod("SlotLabel", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { 0 });
            string format = (string)typeof(Strings).GetProperty("Status_SlotFailed_Format")?.GetValue(Strings.Instance);
            Assert.False(string.IsNullOrEmpty(format));
            Assert.Equal(string.Format(format, label), shown);
        }

        private static string FindRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
