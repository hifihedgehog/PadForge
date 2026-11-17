using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The tablet reader walks the HID collections when a walk is owed, and
    /// does not open again a collection already found to be no tablet.
    ///
    /// <para>Its two-second timer walked every collection each time and
    /// opened every one that is not a tablet to read its descriptor again
    /// (measured 2026-11-16). A device change, a raw input change, a request
    /// that came during a walk and a collection that could not be read each
    /// owe a walk, which the next tick makes, and the timer alone walks every
    /// thirty seconds.</para>
    /// </summary>
    public class TabletWalkTests
    {
        /// <summary>A collection that could not be read is worth another
        /// try, and is not one known to be no tablet.</summary>
        [Fact]
        public void ACollectionThatCannotBeOpenedIsNotCalledNoTablet()
        {
            var open = typeof(PadForge.Engine.Tablets.WindowsTabletReader).Assembly
                .GetType("PadForge.Engine.Tablets.TabletReportDescriptor")
                .GetMethod("TryOpen", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(open);
            var args = new object[] { @"\\?\hid#padforge_no_such_device#0#{4d1e55b2-f16f-11cf-88cb-001111000030}", "HID\\NONE\\0", null };
            Assert.Null(open.Invoke(null, args));
            Assert.False((bool)args[2]);
        }

        [Fact]
        public void TheTimerWalksOnlyWhenAWalkIsOwed()
        {
            string reader = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.Engine", "Common", "Tablets", "WindowsTabletReader.cs")).Replace("\r\n", "\n");
            Assert.Contains("internal const int FallbackWalkMs = 30000;", reader);
            Assert.Contains("timer = new Timer(_ => ScheduleEnumeration(owe: false), null, 0, 2000);", reader);
            Assert.Contains("else if (Volatile.Read(ref owed) == 0 && Environment.TickCount64 - Volatile.Read(ref lastWalk) < FallbackWalkMs) return;", reader);
            Assert.Contains("if (notTablets.Contains(item.Path)) continue;", reader);
            Assert.Contains("if (notATablet) notTablets.Add(item.Path);\n                            else Volatile.Write(ref owed, 1);", reader);
            // A device change or a raw input change still walks at once.
            Assert.Contains("ScheduleEnumeration();\n            }\n        }\n        catch (Exception error) { SdlDiagLog.WriteLine($\"TABLET input failed", reader);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}
