using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The test host refuses the shared HID helpers, each helper asks before
    /// it opens anything, and nothing in the app turns the refusal on. One
    /// test lifts the refusal to see it come back, so the class runs with
    /// no other beside it.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class HidDevicesOffLimitsTests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Input(string file)
            => File.ReadAllText(Path.Combine(Root(), "PadForge.App", "Common", "Input", file));

        [Fact]
        public void TheTestHostRefusesRealHidDevices()
        {
            Assert.True(RealHidAccess.Refused);
            const string keyboard = @"\\?\hid#vid_048d&pid_c193&mi_01&col05#padforge-test#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            Assert.Null(HidReportChannel.Open(keyboard));
            Assert.Null(HidReportChannel.OpenFeatureOnly(keyboard));
            Assert.False(HidReportChannel.SetFeatureOnce(keyboard, new byte[] { 0x06, 0x01 }, 2000));
            Assert.False(HidReportChannel.WriteOutputOnce(keyboard, new byte[] { 0x00, 0x05 }, 2000));
            Assert.False(RawHidOutput.Write(keyboard, new byte[] { 0x00, 0x05 }));
            Assert.False(RawHidOutput.WriteOnce(keyboard, new byte[] { 0x00, 0x05 }));
            Assert.False(RawHidOutput.SetFeature(keyboard, new byte[] { 0x06, 0x01 }, stampSonyBtCrc: false));
            using var reader = new VendorHidReader(new VendorHidCollection { Path = keyboard });
            Assert.False(reader.Open());
        }

        [Fact]
        public void AnAllowScopeLiftsTheRefusal_AndPutsItBack()
        {
            using (RealHidAccess.AllowForTest())
            {
                Assert.False(RealHidAccess.Refused);
                using (RealHidAccess.AllowForTest())
                    Assert.False(RealHidAccess.Refused);
                Assert.False(RealHidAccess.Refused);
            }
            Assert.True(RealHidAccess.Refused);
        }

        /// <summary>A scope that lifts a refusal lifts it for the whole
        /// test host. A class running beside the one that asked would
        /// reach real devices too, and a class that checks the refusal
        /// would find it gone. So each class that lifts one, for the HID
        /// helpers or for the TouchSense mice, is in the collection that
        /// runs with no other class beside it.</summary>
        [Fact]
        public void EveryClassThatLiftsARefusal_RunsWithNoOtherClassBesideIt()
        {
            string root = Root();
            char slash = Path.DirectorySeparatorChar;
            // Put together here, so this test is not one of the places it looks for.
            string lift = "." + "AllowForTest()";
            const string alone = "[Collection(\"PeripheralOutputStatics\")]";
            var declaration = new Regex(@"^    (?:public|internal)(?: (?:sealed|static|abstract|partial))* class \w+");
            var loose = new List<string>();
            int lifts = 0;
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "PadForge.Tests"), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(slash + "obj" + slash) || file.Contains(slash + "bin" + slash)) continue;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains(lift, StringComparison.Ordinal)) continue;
                    lifts++;
                    // The class the line is in: the nearest one declared
                    // above it at the namespace's own indent.
                    int at = i;
                    while (at >= 0 && !declaration.IsMatch(lines[at])) at--;
                    bool runsAlone = false;
                    for (int j = at - 1; j >= 0 && lines[j].TrimStart().StartsWith("[", StringComparison.Ordinal); j--)
                        runsAlone |= lines[j].Trim() == alone;
                    if (!runsAlone) loose.Add($"{Path.GetFileName(file)}:{i + 1}");
                }
            }
            Assert.Empty(loose);
            // The scan found the scopes it is about, this class's own two among them.
            Assert.True(lifts >= 2, lifts.ToString());
            Assert.Contains("[CollectionDefinition(\"PeripheralOutputStatics\", DisableParallelization = true)]",
                File.ReadAllText(Path.Combine(root, "PadForge.Tests", "PeripheralOutputsTests.cs")));
        }

        /// <summary>Each helper asks before its first CreateFile and before
        /// it caches anything for the path, so a refusal opens no handle.</summary>
        [Theory]
        [InlineData("HidReportChannel.cs", "private static HidReportChannel Open(string path, bool featureOnly)")]
        [InlineData("RawHidOutput.cs", "public static bool Write(string devicePath, byte[] buf)")]
        [InlineData("RawHidOutput.cs", "public static bool WriteOnce(string devicePath, byte[] buf, int timeoutMs = 1000)")]
        [InlineData("RawHidOutput.cs", "public static bool SetFeature(string devicePath, byte[] buf, bool stampSonyBtCrc)")]
        [InlineData("RawHidOutput.cs", "public static bool SetFeatureOwnLengthFirst(string devicePath, byte[] report)")]
        [InlineData("VendorHidRuntime.cs", "public bool Open()")]
        public void EachHelperAsksBeforeItOpens(string file, string signature)
        {
            string source = Input(file);
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature);
            Assert.Equal(-1, source.IndexOf(signature, at + 1, StringComparison.Ordinal));
            int asks = source.IndexOf("RealHidAccess.Refused", at, StringComparison.Ordinal);
            Assert.True(asks > at, signature + " never asks");
            string before = source.Substring(at + signature.Length, asks - at - signature.Length);
            Assert.DoesNotContain("CreateFile", before);
            Assert.DoesNotContain("GetOrAdd", before);
            // The ask sits in this method, not in one further down the file.
            Assert.DoesNotContain(" static ", before);
            Assert.DoesNotContain("public ", before);
            Assert.DoesNotContain("private ", before);
        }

        [Fact]
        public void NothingInTheAppTurnsTheRefusalOn()
        {
            string root = Root();
            char slash = Path.DirectorySeparatorChar;
            var callers = new[] { "PadForge.App", "PadForge.Engine" }
                .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                .Where(path => !path.Contains(slash + "obj" + slash) && !path.Contains(slash + "bin" + slash))
                .Where(path => File.ReadAllText(path).Contains("SetOffLimitsForTest", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();
            Assert.Equal(new[] { "RealHidAccess.cs" }, callers);
        }
    }
}
