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
    /// (measured 2026-11-16). A device change, a raw input change and a
    /// request that came during a walk each owe a walk, which the next tick
    /// makes, and the timer alone walks every thirty seconds. A collection
    /// that could not be read owes none.</para>
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
            Assert.Contains("skips.BeginWalk(retry);", reader);
            Assert.Contains("if (skips.Skip(item.Path)) continue;", reader);
            Assert.Contains("if (notATablet) skips.NoteNotATablet(item.Path);\n                            else\n                            {\n                                skips.NoteUnreadable(item.Path);", reader);
            Assert.Contains("skips.KeepPresent(present);", reader);
            // A device change or a raw input change still walks at once.
            Assert.Contains("ScheduleEnumeration();\n            }\n        }\n        catch (Exception error) { SdlDiagLog.WriteLine($\"TABLET input failed", reader);
        }

        /// <summary>A collection that could not be read owes no walk. The
        /// timer's walks pass it by, and a device or raw input change, or
        /// the thirty-second walk, tries it again. It owed a walk on every
        /// walk, so one collection that never opened kept the full walk
        /// running every two seconds.</summary>
        [Fact]
        public void ACollectionThatCannotBeRead_IsTriedAgainOnlyOnAChangeOrTheSlowWalk()
        {
            const string unreadable = @"\\?\hid#unreadable#0", keyboard = @"\\?\hid#keyboard#0";
            var skips = new PadForge.Engine.Tablets.TabletWalkSkips();
            skips.BeginWalk(retryUnreadable: false);
            Assert.False(skips.Skip(unreadable));
            skips.NoteNotATablet(keyboard);
            skips.NoteUnreadable(unreadable);

            // A walk owed for another reason does not open it again.
            skips.BeginWalk(retryUnreadable: false);
            Assert.True(skips.Skip(unreadable));
            Assert.True(skips.Skip(keyboard));

            // A change tries it again, and still knows the keyboard.
            skips.BeginWalk(retryUnreadable: true);
            Assert.False(skips.Skip(unreadable));
            Assert.True(skips.Skip(keyboard));

            // A path that left is read again if it comes back.
            skips.NoteUnreadable(unreadable);
            skips.KeepPresent(new System.Collections.Generic.HashSet<string> { @"\\?\hid#other#0" });
            Assert.False(skips.Skip(unreadable));
            Assert.False(skips.Skip(keyboard));
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
