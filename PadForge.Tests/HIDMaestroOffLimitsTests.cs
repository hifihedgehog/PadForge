using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HIDMaestro;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The test host makes no HIDMaestro context that can write to the PC
    /// (HIDMaestroOffLimits). Each test watches the decision through
    /// <see cref="HMaestroContexts.MakeSeam"/>, so a broken guard shows here
    /// without a real context being made.
    /// </summary>
    public class HIDMaestroOffLimitsTests
    {
        [Fact]
        public void TheTestHost_MakesNoContextForDevices()
        {
            Assert.True(HMaestroContexts.OffLimitsForTest);
            var made = new List<HMBackend>();
            HMaestroContexts.MakeSeam = b => { made.Add(b); return null; };
            try
            {
                Assert.Throws<InvalidOperationException>(() => HMaestroContexts.ForDevices());
                Assert.Empty(made);
            }
            finally
            {
                HMaestroContexts.MakeSeam = null;
            }
        }

        [Fact]
        public void TheCatalogsContext_IsAUsbipOne_InTheTestHost()
        {
            Assert.True(HMaestroContexts.OffLimitsForTest);
            var made = new List<HMBackend>();
            HMaestroContexts.MakeSeam = b => { made.Add(b); return null; };
            try
            {
                HMaestroContexts.ForProfiles();
                Assert.Equal(new[] { HMBackend.Usbip }, made);
            }
            finally
            {
                HMaestroContexts.MakeSeam = null;
            }
        }

        [Fact]
        public void TheTestHost_RunsNoSweepOrRemoval_AndReadsNoBackend()
        {
            Assert.True(HMaestroContexts.OffLimitsForTest);
            var called = new List<string>();
            HMaestroContexts.CallSeam = called.Add;
            try
            {
                HMaestroContexts.SweepEndedPrograms();
                Assert.Null(HMaestroContexts.Uninstall(null));
                Assert.Throws<InvalidOperationException>(() => HMaestroContexts.MachineBackend());
                Assert.Throws<InvalidOperationException>(() => HMaestroContexts.UsbipBackendAvailable());
                Assert.Empty(called);
            }
            finally
            {
                HMaestroContexts.CallSeam = null;
            }
        }

        /// <summary>The SDK's static sweep, removal and backend reads are
        /// called in the factory alone, behind its guard.</summary>
        [Fact]
        public void TheSdksStaticCalls_AreMadeOnlyInTheFactory()
        {
            var call = new Regex(@"HMContext\.(RemoveAllVirtualControllers|MachineBackend|IsUsbipBackendAvailable)|HMUninstall\.Run\s*\(", RegexOptions.Compiled);
            var quoted = new Regex(@"""[^""]*""", RegexOptions.Compiled);
            var found = new List<string>();
            string root = OffLimitsSource.RepoRoot();
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (!Directory.EnumerateFiles(dir, "*.csproj").Any()) continue;
                foreach (string path in OffLimitsSource.Sources(Path.GetFileName(dir)))
                {
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string code = quoted.Replace(line.TrimStart(), "\"\"");
                        if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                        if (call.IsMatch(code)) found.Add(Path.GetFileName(path) + ": " + code);
                    }
                }
            }
            Assert.All(found, f => Assert.StartsWith("HMaestroContexts.cs: ", f, StringComparison.Ordinal));
            Assert.Equal(4, found.Count);
        }

        /// <summary>No project of the repository makes a context anywhere
        /// but the factory, so no path of the application or the tests gets
        /// past its guard. A test that reads profiles takes the catalog's
        /// context from the factory too.</summary>
        [Fact]
        public void OnlyTheFactory_MakesAContext()
        {
            var made = new Regex(@"new\s+(HIDMaestro\.)?HMContext\s*\(", RegexOptions.Compiled);
            // A test that pins a file's text names the call in a string.
            var quoted = new Regex(@"""[^""]*""", RegexOptions.Compiled);
            var found = new List<string>();
            int read = 0;
            string root = OffLimitsSource.RepoRoot();
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (!Directory.EnumerateFiles(dir, "*.csproj").Any()) continue;
                foreach (string path in OffLimitsSource.Sources(Path.GetFileName(dir)))
                {
                    read++;
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string code = quoted.Replace(line.TrimStart(), "\"\"");
                        if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                        if (made.IsMatch(code)) found.Add(Path.GetFileName(path) + ": " + code);
                    }
                }
            }
            Assert.True(read > 200, "the repository's sources were not found");
            Assert.All(found, f => Assert.StartsWith("HMaestroContexts.cs: ", f, StringComparison.Ordinal));
            Assert.Single(found);
        }
    }
}
