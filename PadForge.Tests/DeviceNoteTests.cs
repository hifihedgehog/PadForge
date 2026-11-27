using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Search;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Kind = PadForge.Services.DeviceNoteKind;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The rule for one device of the device notes (#597, #600 item 42),
    /// <see cref="DeviceNoteDirector.Decide"/>: what is said of a mapped
    /// device that disconnects or connects, and when. Pure, so each test
    /// names what a walk reads at each moment and reads what is said.
    /// </summary>
    public class DeviceNoteDecisionTests
    {
        /// <summary>One device read every quarter second, and what was said
        /// of it with the moment it was said. <see cref="Own"/> is the stamp
        /// of PadForge's own work that covers it, and <see cref="Screen"/>
        /// what the note on screen says of it.</summary>
        private sealed class Device
        {
            public DeviceNoteState State;
            public bool Drops = true, Returns = true, Waiting;
            public long Now;
            public long Own = DeviceNoteDirector.Never, OwnRow = DeviceNoteDirector.Never;
            public DeviceNoteScreen Screen;
            public readonly List<string> Said = new List<string>();

            public Device(bool online) => State = DeviceNoteState.Seed(online, 0);

            /// <summary>Reads the device as online or not until the clock
            /// stands at <paramref name="untilMs"/>.</summary>
            public Device Read(bool online, long untilMs)
            {
                while (Now < untilMs)
                {
                    Now += DeviceNoteDirector.WalkMs;
                    var (next, note, inPlace) = DeviceNoteDirector.Decide(State, online, Now, Drops, Returns, Waiting, Own, Screen, OwnRow);
                    State = next;
                    if (note != Kind.None) Said.Add(Now + " " + note + (inPlace ? " in place" : ""));
                }
                return this;
            }
        }

        /// <summary>A drop is said at the first walk that reads it (#600
        /// item 42), a quarter second away included, and counted.</summary>
        [Fact]
        public void ADrop_IsSaidAtTheFirstWalkThatReadsIt_AShortOneToo()
        {
            var held = new Device(online: true).Read(true, 1000).Read(false, 1250);
            Assert.Equal(new[] { "1250 Disconnected" }, held.Said);
            Assert.Equal(1250, held.State.Drop1);
            held.Read(false, 30_000);
            Assert.Single(held.Said);

            // Away for one walk: said and counted all the same.
            var brief = new Device(online: true).Read(true, 1000).Read(false, 1250).Read(true, 30_000);
            Assert.Equal(new[] { "1250 Disconnected", "11250 Connected" }, brief.Said);
            Assert.Equal(1250, brief.State.Drop1);
        }

        [Fact]
        public void ADeviceAwayWhenFirstRead_SaysNothingUntilItConnects()
        {
            var device = new Device(online: false).Read(false, 60_000);
            Assert.Empty(device.Said);
            // That absence was never seen to begin, so it is no drop.
            Assert.Equal(DeviceNoteDirector.Never, device.State.Drop1);

            device.Read(true, 61_000);
            Assert.Equal(new[] { "60250 Connected" }, device.Said);
        }

        /// <summary>One new note for a device in ten seconds. A return
        /// inside that time, with no note on screen to change, is said when
        /// the time ends, so the last thing said of the device is
        /// true.</summary>
        [Fact]
        public void ADeviceIsNamedOnceInTenSeconds_AndWhatStillStandsIsSaidAfter()
        {
            var device = new Device(online: true).Read(false, 2000);
            Assert.Equal(new[] { "250 Disconnected" }, device.Said);

            // Back at 2250, and nothing more is said until 10250.
            device.Read(true, 10_000);
            Assert.Single(device.Said);
            device.Read(true, 10_500);
            Assert.Equal(new[] { "250 Disconnected", "10250 Connected" }, device.Said);
        }

        /// <summary>A return that ends inside the ten seconds is never said
        /// as a new note: what the user was told last still stands. Its
        /// drop counts.</summary>
        [Fact]
        public void AChangeThatEndsInsideTheTenSeconds_IsNeverSaid_AndItsDropCounts()
        {
            var device = new Device(online: true).Read(false, 2000).Read(true, 4000).Read(false, 30_000);
            Assert.Equal(new[] { "250 Disconnected" }, device.Said);
            Assert.Equal(4250, device.State.Drop1);
            Assert.Equal(250, device.State.Drop2);
        }

        [Fact]
        public void ThreeDropsInAMinute_AreSaidOnceWithTheCount_ThenFiveMinutesOfSilence()
        {
            var device = new Device(online: true)
                .Read(false, 2000).Read(true, 20_000)      // the first drop, at 250
                .Read(false, 22_000).Read(true, 40_000)    // the second, at 20250
                .Read(false, 42_000);                      // the third, at 40250
            Assert.Equal(new[]
            {
                "250 Disconnected", "10250 Connected", "20250 Disconnected", "30250 Connected", "40250 KeepsDisconnecting",
            }, device.Said);
            Assert.Equal(3, device.State.KeepsCount);
            Assert.Equal(40_250 + DeviceNoteDirector.SilenceMs, device.State.SilentUntil);

            // Five minutes in which it comes and goes and nothing is said.
            device.Said.Clear();
            for (long at = 50_000; at < 340_000; at += 20_000)
                device.Read(true, at).Read(false, at + 10_000);
            Assert.Empty(device.Said);

            // After them the device says what happens to it again. It was
            // last read away, which nothing is said of now. Its return is
            // said, and its next drop as a first one.
            device.Read(false, 342_000).Read(true, 343_000).Read(false, 360_000);
            Assert.Equal(new[] { "342250 Connected", "352250 Disconnected" }, device.Said);
            Assert.False(device.State.KeepsOwed);
        }

        /// <summary>Five drops of a second each, twelve seconds apart, the
        /// bad cable the hold used to hide: the first is said and the third
        /// is the count.</summary>
        [Fact]
        public void ShortDropsTwelveSecondsApart_ReachTheCount()
        {
            var device = new Device(online: true).Read(true, 1000);
            for (int i = 0; i < 5; i++)
                device.Read(false, device.Now + 1000).Read(true, device.Now + 11_000);
            // The second drop falls inside the ten seconds of the return's
            // note: counted, not said.
            Assert.Equal(new[] { "1250 Disconnected", "11250 Connected", "25250 KeepsDisconnecting" }, device.Said);
            Assert.Equal(3, device.State.KeepsCount);
        }

        [Fact]
        public void ThreeDropsOverMoreThanAMinute_AreThreePlainNotes()
        {
            var device = new Device(online: true)
                .Read(false, 2000).Read(true, 40_000)
                .Read(false, 42_000).Read(true, 80_000)
                .Read(false, 82_000);
            Assert.DoesNotContain(device.Said, s => s.EndsWith("KeepsDisconnecting", StringComparison.Ordinal));
            Assert.Equal(3, device.Said.Count(s => s.EndsWith(" Disconnected", StringComparison.Ordinal)));
            Assert.Equal(DeviceNoteDirector.Never, device.State.SilentUntil);
        }

        /// <summary>The count is one note too, so a third drop that comes
        /// inside the ten seconds of the last note is said when they end,
        /// and a drop while it waits adds to the count.</summary>
        [Fact]
        public void TheCount_WaitsOutTheTenSeconds_AndGrowsWhileItWaits()
        {
            var device = new Device(online: true)
                .Read(false, 1000).Read(true, 2000)     // counted at 250, said
                .Read(false, 3000).Read(true, 4000)     // counted at 2250
                .Read(false, 5000);                     // counted at 4250: the third
            Assert.Equal(new[] { "250 Disconnected" }, device.Said);
            Assert.True(device.State.KeepsOwed);
            Assert.Equal(3, device.State.KeepsCount);

            device.Read(true, 6000).Read(false, 7000).Read(true, 10_000);    // a fourth at 6250
            Assert.Equal(4, device.State.KeepsCount);
            Assert.Single(device.Said);
            device.Read(true, 11_000);
            Assert.Equal(new[] { "250 Disconnected", "10250 KeepsDisconnecting" }, device.Said);
            Assert.Equal(10_250 + DeviceNoteDirector.SilenceMs, device.State.SilentUntil);
        }

        /// <summary>A note that is off is taken as said: it uses up no ten
        /// seconds, and nothing is owed when it is turned on.</summary>
        [Fact]
        public void ANoteThatIsOff_IsTakenAsSaid()
        {
            var returnsOnly = new Device(online: true) { Drops = false };
            returnsOnly.Read(false, 3000);
            Assert.Empty(returnsOnly.Said);
            Assert.False(returnsOnly.State.Told);
            Assert.Equal(DeviceNoteDirector.Never, returnsOnly.State.LastNote);
            returnsOnly.Read(true, 4000);
            Assert.Equal(new[] { "3250 Connected" }, returnsOnly.Said);

            var dropsOnly = new Device(online: true) { Returns = false };
            dropsOnly.Read(false, 2000).Read(true, 4000);
            Assert.Equal(new[] { "250 Disconnected" }, dropsOnly.Said);
            Assert.True(dropsOnly.State.Told);
            // Turned on now, the note has nothing to say of that return.
            dropsOnly.Returns = true;
            dropsOnly.Read(true, 30_000);
            Assert.Single(dropsOnly.Said);
        }

        /// <summary>With the Device Disconnected Note off, three drops in a
        /// minute still quiet the device for five minutes, and no line
        /// shows.</summary>
        [Fact]
        public void WithTheDisconnectedNoteOff_ThreeDropsQuietTheDeviceWithNoLine()
        {
            var device = new Device(online: true) { Drops = false };
            device.Read(false, 2000).Read(true, 20_000)
                  .Read(false, 22_000).Read(true, 40_000)
                  .Read(false, 42_000);
            Assert.Equal(new[] { "2250 Connected", "22250 Connected", "40250 Silenced" }, device.Said);

            device.Said.Clear();
            device.Read(true, 100_000).Read(false, 200_000).Read(true, 300_000);
            Assert.Empty(device.Said);
        }

        /// <summary>While the flyout shows a profile switch nothing is
        /// decided. A drop that outlasts the wait is said after it, and one
        /// that ends inside it never is.</summary>
        [Fact]
        public void WhileNotesWait_AnAbsenceKeepsItsStart_AndNothingIsDecided()
        {
            var outlasts = new Device(online: true) { Waiting = true };
            outlasts.Read(false, 6000);
            Assert.Empty(outlasts.Said);
            Assert.Equal(250, outlasts.State.AwaySince);
            Assert.False(outlasts.State.DropCounted);
            outlasts.Waiting = false;
            outlasts.Read(false, 7000);
            Assert.Equal(new[] { "6250 Disconnected" }, outlasts.Said);

            var ends = new Device(online: true) { Waiting = true };
            ends.Read(false, 4000).Read(true, 6000);
            ends.Waiting = false;
            ends.Read(true, 30_000);
            Assert.Empty(ends.Said);
            Assert.Equal(DeviceNoteDirector.Never, ends.State.Drop1);
        }

        // ── PadForge's own work ────────────────────────────────────────

        /// <summary>A drop that begins within five seconds of a stamp of
        /// PadForge's own work (a virtual controller's creation, a MIDI
        /// backend change) says nothing and counts toward nothing, however
        /// often it comes.</summary>
        [Fact]
        public void AnOwnWorkDrop_SaysNothing_AndCountsTowardNothing()
        {
            var device = new Device(online: true).Read(true, 1000);
            for (int i = 0; i < 5; i++)
            {
                device.Own = device.Now - 100;
                device.Read(false, device.Now + 2000).Read(true, device.Now + 3000);
            }
            Assert.Empty(device.Said);
            Assert.Equal(DeviceNoteDirector.Never, device.State.Drop1);
            Assert.False(device.State.KeepsOwed);
            Assert.True(device.State.Told);
        }

        /// <summary>A row an own cause took away that is still away when the
        /// five seconds from the stamp end is a drop then: said and
        /// counted.</summary>
        [Fact]
        public void AnOwnWorkDropThatOutlastsItsWindow_IsSaidWhenItEnds()
        {
            var device = new Device(online: true) { Own = 1000 }.Read(true, 1000).Read(false, 10_000);
            Assert.Equal(new[] { "6000 Disconnected" }, device.Said);
            Assert.Equal(6000, device.State.Drop1);
        }

        /// <summary>A stamp more than five seconds old excuses nothing. One
        /// read after the walk's clock (made between the clock and the row's
        /// read) is as recent as can be.</summary>
        [Fact]
        public void AStampOutsideTheWindow_ExcusesNothing()
        {
            var old = new Device(online: true) { Own = 1000 }.Read(true, 6250).Read(false, 6500);
            Assert.Equal(new[] { "6500 Disconnected" }, old.Said);
            var oldRow = new Device(online: true) { OwnRow = 1000 }.Read(true, 6250).Read(false, 6500);
            Assert.Equal(new[] { "6500 Disconnected" }, oldRow.Said);

            var later = new Device(online: true) { Own = 1260 }.Read(true, 1000).Read(false, 3000);
            Assert.Empty(later.Said);
        }

        /// <summary>A row PadForge took offline itself (its feature switched
        /// off, its MIDI inputs closed) is PadForge's until it returns, a
        /// minute away included: nothing said, nothing counted.</summary>
        [Fact]
        public void ARowPadForgeTookOffline_IsItsUntilItReturns()
        {
            var device = new Device(online: true) { OwnRow = 900 }.Read(true, 1000).Read(false, 61_000);
            Assert.Empty(device.Said);
            Assert.Equal(DeviceNoteDirector.Never, device.State.Drop1);
            device.Read(true, 62_000);
            Assert.Empty(device.Said);

            // Its next drop, with no new stamp, is said.
            device.Read(false, 62_250);
            Assert.Equal(new[] { "62250 Disconnected" }, device.Said);
        }

        // ── the note on screen ─────────────────────────────────────────

        /// <summary>While the note on screen names the device alone, the
        /// note follows it in place with no ten seconds to wait: back, away
        /// again, back, and the third drop turns it to the count, which
        /// then grows in place.</summary>
        [Fact]
        public void TheNoteOnScreen_TurnsToTheReturn_BackToTheDrop_AndToTheCount()
        {
            var device = new Device(online: true).Read(false, 250);
            Assert.Equal(new[] { "250 Disconnected" }, device.Said);
            device.Screen = DeviceNoteScreen.Alone;
            device.Read(false, 1000).Read(true, 1250);
            device.Read(true, 2000).Read(false, 2250);
            device.Read(false, 2500).Read(true, 2750);
            device.Read(true, 3000).Read(false, 3250);
            Assert.Equal(new[]
            {
                "250 Disconnected", "1250 IsBack in place", "2250 Disconnected in place", "2750 IsBack in place", "3250 KeepsDisconnecting in place",
            }, device.Said);
            Assert.Equal(3, device.State.KeepsCount);
            Assert.Equal(3250 + DeviceNoteDirector.SilenceMs, device.State.SilentUntil);

            // Its count on screen grows with each drop, and its returns
            // leave it as it is.
            device.Screen = DeviceNoteScreen.Keeps;
            device.Read(true, 3500).Read(false, 3750).Read(true, 4000);
            Assert.Equal("3750 KeepsDisconnecting in place", device.Said.Last());
            Assert.Equal(4, device.State.KeepsCount);

            // Gone from the screen: the silence.
            device.Screen = DeviceNoteScreen.None;
            device.Said.Clear();
            device.Read(false, 5000).Read(true, 6000);
            Assert.Empty(device.Said);
        }

        /// <summary>The turn to "is back" belongs to the drop note, so the
        /// Device Disconnected Note gates it, not the connected one.</summary>
        [Fact]
        public void TheTurnInPlace_FollowsTheDisconnectedNote()
        {
            var noReturns = new Device(online: true) { Returns = false }.Read(false, 250);
            noReturns.Screen = DeviceNoteScreen.Alone;
            noReturns.Read(true, 500);
            Assert.Equal(new[] { "250 Disconnected", "500 IsBack in place" }, noReturns.Said);

            var noDrops = new Device(online: true) { Drops = false, Screen = DeviceNoteScreen.Alone };
            noDrops.Read(false, 250).Read(true, 500);
            Assert.DoesNotContain(noDrops.Said, s => s.EndsWith("in place", StringComparison.Ordinal));
        }

        /// <summary>One device of a burst on screen turns in place too, and
        /// a burst cannot become one device's count: a third drop there
        /// waits for its own line.</summary>
        [Fact]
        public void ADeviceOfABurstOnScreen_TurnsInPlace_AndItsCountWaits()
        {
            var device = new Device(online: true).Read(false, 250);
            device.Screen = DeviceNoteScreen.InBurst;
            device.Read(true, 500).Read(false, 750).Read(true, 1000).Read(false, 1250);
            Assert.Equal(new[] { "250 Disconnected", "500 IsBack in place", "750 Disconnected in place", "1000 IsBack in place", "1250 Disconnected in place" }, device.Said);
            Assert.True(device.State.KeepsOwed);
            device.Screen = DeviceNoteScreen.None;
            device.Read(false, 12_000);
            Assert.Equal("11250 KeepsDisconnecting", device.Said.Last());
        }

        /// <summary>A virtual controller's bring-up that outlasts the five
        /// seconds: every row counts as stamped now for as long as it runs,
        /// so an absence that began inside it stays PadForge's own work until
        /// five seconds after the bring-up's last stamp.</summary>
        [Fact]
        public void AnAbsenceInsideALongBringUp_IsOwnWorkForAsLongAsTheBringUpRuns()
        {
            var device = new Device(online: true).Read(true, 1000);
            // The row is away from 1250, and the bring-up stamps every walk
            // up to 9000.
            while (device.Now < 9000)
            {
                device.Own = device.Now + DeviceNoteDirector.WalkMs;
                device.Read(false, device.Now + DeviceNoteDirector.WalkMs);
            }
            // The bring-up ended at 9000. The row returns at 10000.
            device.Read(false, 9750).Read(true, 30_000);
            Assert.Empty(device.Said);
            Assert.Equal(DeviceNoteDirector.Never, device.State.Drop1);
        }
    }

    /// <summary>
    /// The walk of the device notes (#597), <see cref="DeviceNoteDirector"/>,
    /// read with no window and no engine: what it says of the rows a test
    /// hands it, and all the times it says nothing.
    /// </summary>
    public class DeviceNoteDirectorTests
    {
        /// <summary>The mapped rows, the clock, what the flyout allows, and
        /// the lines the director answered with.</summary>
        private sealed class Stage
        {
            public readonly DeviceNoteDirector Director = new DeviceNoteDirector();
            public readonly List<DeviceNoteReading> Mapped = new List<DeviceNoteReading>();
            public readonly Dictionary<Guid, string> Names = new Dictionary<Guid, string>();
            public readonly List<string> Said = new List<string>();
            public readonly List<string> Quieted = new List<string>();
            /// <summary>The devices each line for several names, in
            /// order (#600 item 42).</summary>
            public readonly List<string> Named = new List<string>();
            public long Now;
            /// <summary>When the enumeration the rows come from began. Null
            /// reads them as fresh at every walk.</summary>
            public long? RowsReadAt;
            public bool Drops = true, Returns = true, Waiting, Free = true;

            /// <summary>Plays the flyout: a line shows for its time (a drop
            /// or a count four seconds, a return two), the walk is told it is
            /// up, and a line in place gives it its time again unless only
            /// the count grew. Off, <see cref="Free"/> is set by hand and no
            /// note is ever up.</summary>
            public bool Screen;
            private long _upUntil = DeviceNoteDirector.Never;

            public Stage() => Director.Silenced = (device, drops) => Quieted.Add(Names[device]);

            public Guid Add(string name, bool online, int slots = 1, bool raises = true)
            {
                var device = Guid.NewGuid();
                Names[device] = name;
                Mapped.Add(new DeviceNoteReading(device, online, slots, raises));
                return device;
            }

            /// <summary>The device's row as the next walk reads it, with the
            /// stamps of PadForge's own work that cover every row and that
            /// name it.</summary>
            public void Set(Guid device, bool online, long own = DeviceNoteDirector.Never, long ownRow = DeviceNoteDirector.Never)
            {
                int at = Mapped.FindIndex(r => r.Device == device);
                Mapped[at] = new DeviceNoteReading(device, online, Mapped[at].Slots, Mapped[at].RaisesNotes, own, ownRow);
            }

            public void Remove(Guid device) => Mapped.RemoveAll(r => r.Device == device);

            public void Tick()
            {
                bool shown = Screen && Now < _upUntil;
                if (Screen) Free = !shown;
                var note = Director.Tick(Now, RowsReadAt ?? Now, Mapped, Drops, Returns, Waiting, Free, shown);
                if (note.Kind == Kind.None) return;
                if (Screen && !note.Grows)
                    _upUntil = Now + (note.Kind is Kind.Connected or Kind.IsBack ? 2000 : 4000);
                string line = Now + " " + note.Kind + (note.Count == 1 ? " " + Names[note.Device] : " x" + note.Count);
                if (note.EmptiedSlot >= 0) line += " empties " + note.EmptiedSlot;
                if (note.Times > 0) line += " times " + note.Times;
                if (note.InPlace) line += " in place";
                Said.Add(line);
                if (note.Count > 1)
                {
                    Assert.Equal(note.Count, note.Devices.Count);
                    Named.Add(string.Join(",", note.Devices.Select(d => Names[d])));
                }
            }

            /// <summary>Walks every quarter second until the clock stands at
            /// <paramref name="untilMs"/>.</summary>
            public Stage Walk(long untilMs)
            {
                while (Now < untilMs)
                {
                    Now += DeviceNoteDirector.WalkMs;
                    Tick();
                }
                return this;
            }
        }

        // ── nothing is said of what a walk finds ───────────────────────

        /// <summary>Startup: the first walk reads each device as it is, and
        /// nothing is said of a device that was connected all along or of
        /// one that is away.</summary>
        [Fact]
        public void TheFirstWalk_SaysNothingOfWhatItFinds()
        {
            var stage = new Stage();
            var pad = stage.Add("Pad", online: true);
            var wheel = stage.Add("Wheel", online: false);
            stage.Walk(30_000);
            Assert.Empty(stage.Said);
            Assert.True(stage.Director.Knows(pad) && stage.Director.Knows(wheel));

            // A change after it is said: the half second devices are
            // gathered in comes after the walk that reads it.
            stage.Set(wheel, true);
            stage.Walk(31_000);
            Assert.Equal(new[] { "30750 Connected Wheel" }, stage.Said);
        }

        /// <summary>An engine start: nothing is read until an enumeration
        /// that began five seconds after it has finished, so a controller
        /// the first enumeration missed and the next one found is one that
        /// was connected all along.</summary>
        [Fact]
        public void AfterAnEngineStart_TheRowsAreReadOnceAnEnumerationFromWellAfterItHasFinished()
        {
            var stage = new Stage { Now = 100_000 };
            var pad = stage.Add("Pad", online: true);
            var late = stage.Add("Late Pad", online: false);
            stage.Director.Mark(100_000);

            // The engine has listed nothing yet.
            stage.RowsReadAt = DeviceNoteDirector.Never;
            stage.Walk(100_500);
            Assert.False(stage.Director.Knows(pad));

            // Its first enumeration, at once, and the one two seconds
            // later, which finds the late pad. Both began too early.
            stage.RowsReadAt = 100_020;
            stage.Walk(102_000);
            stage.RowsReadAt = 102_020;
            stage.Set(late, true);
            stage.Walk(104_000);
            stage.RowsReadAt = 104_020;
            stage.Walk(106_000);
            Assert.False(stage.Director.Knows(pad));
            Assert.Empty(stage.Said);

            // The first one that began five seconds after the start.
            stage.RowsReadAt = 106_020;
            stage.Walk(106_250);
            Assert.True(stage.Director.Knows(pad) && stage.Director.Knows(late));
            stage.Walk(120_000);
            Assert.Empty(stage.Said);

            // From here a drop is said.
            stage.RowsReadAt = null;
            stage.Set(pad, false);
            stage.Walk(123_000);
            Assert.Equal(new[] { "120750 Disconnected Pad" }, stage.Said);
        }

        /// <summary>An engine stop forgets every device and every line, so
        /// nothing is said of the rows it takes offline.</summary>
        [Fact]
        public void AnEngineStop_ForgetsEveryDeviceAndEveryLine()
        {
            var stage = new Stage();
            var pad = stage.Add("Pad", online: true);
            stage.Walk(1000);
            // A drop that is decided and waits out its half second.
            stage.Set(pad, false);
            stage.Walk(1250);
            Assert.Empty(stage.Said);

            stage.Director.Reset();
            Assert.False(stage.Director.Knows(pad));
            stage.Walk(10_000);
            Assert.Empty(stage.Said);
        }

        /// <summary>A profile switch changes what is mapped. Every device is
        /// read as for the first time, so the devices the new profile maps
        /// raise no note for being connected or away.</summary>
        [Fact]
        public void AProfileSwitch_ReadsEveryDeviceAsForTheFirstTime()
        {
            var stage = new Stage();
            var pad = stage.Add("Pad", online: true);
            stage.Walk(5000);

            // The new profile maps a wheel that is away and a stick that is
            // connected, and no longer the pad.
            stage.Director.Reset();
            stage.Remove(pad);
            var wheel = stage.Add("Wheel", online: false);
            stage.Add("Stick", online: true);
            stage.Walk(30_000);
            Assert.Empty(stage.Said);

            stage.Set(wheel, true);
            stage.Walk(31_000);
            Assert.Equal(new[] { "30750 Connected Wheel" }, stage.Said);
        }

        /// <summary>While the profile flyout runs its own lines the notes
        /// wait: a drop that outlasts it is said once the flyout is free,
        /// and one that ends before then never is.</summary>
        [Fact]
        public void WhileTheProfileFlyoutShows_NotesWait()
        {
            var stage = new Stage();
            var pad = stage.Add("Pad", online: true, slots: 1);
            var wheel = stage.Add("Wheel", online: true, slots: 2);
            stage.Walk(1000);

            stage.Waiting = true;
            stage.Free = false;
            stage.Set(pad, false);
            stage.Set(wheel, false);
            stage.Walk(4000);
            stage.Set(wheel, true);
            stage.Walk(7000);
            Assert.Empty(stage.Said);

            stage.Waiting = false;
            stage.Free = true;
            stage.Walk(8000);
            Assert.Equal(new[] { "7750 Disconnected Pad empties 0" }, stage.Said);
        }

        /// <summary>Sleep: marked when Windows says it is going to sleep and
        /// again when it wakes. The rows of the moments before the sleep and
        /// of the first moments after it are left unread, and what the first
        /// enumeration from well after the wake finds is not news.</summary>
        [Fact]
        public void AcrossASleep_NothingIsSaid()
        {
            var stage = new Stage();
            var pad = stage.Add("Bluetooth Pad", online: true);
            var wheel = stage.Add("Wheel", online: true);
            stage.Walk(10_000);

            // Windows says it is going to sleep. The pad's radio goes first.
            stage.Director.Mark(stage.Now);
            stage.RowsReadAt = 9000;
            stage.Set(pad, false);
            stage.Walk(11_500);
            Assert.False(stage.Director.Knows(pad));

            // Eight hours later it wakes. The wheel's USB port takes a
            // moment, and the pad stays off.
            stage.Now += 8L * 60 * 60 * 1000;
            long wake = stage.Now;
            stage.Director.Mark(wake);
            stage.Set(wheel, false);
            stage.RowsReadAt = wake + 20;
            stage.Walk(wake + 2000);
            stage.Set(wheel, true);
            stage.RowsReadAt = wake + 2020;
            stage.Walk(wake + 4000);
            stage.RowsReadAt = wake + 4020;
            stage.Walk(wake + 6250);
            Assert.False(stage.Director.Knows(wheel));

            // The first enumeration that began five seconds after the wake.
            stage.RowsReadAt = wake + 6020;
            stage.Walk(wake + 60_000);
            Assert.True(stage.Director.Knows(wheel));
            Assert.Empty(stage.Said);

            // The pad is turned on again. It was connected before the sleep
            // and is connected after it, so nothing is said of it.
            stage.RowsReadAt = null;
            stage.Set(pad, true);
            stage.Walk(wake + 61_000);
            Assert.Empty(stage.Said);

            // Its next drop is news.
            stage.Set(pad, false);
            stage.Walk(wake + 62_000);
            Assert.Equal(new[] { (wake + 61_750) + " Disconnected Bluetooth Pad" }, stage.Said);
        }

        /// <summary>A walk that comes long after the one before it stood
        /// still, as it does through a sleep Windows gave no word of. It is
        /// read as a wake.</summary>
        [Fact]
        public void AWalkAfterAStall_IsReadAsAWake()
        {
            var stage = new Stage();
            var pad = stage.Add("Pad", online: true);
            stage.Walk(10_000);
            Assert.True(stage.Director.Knows(pad));

            stage.Now += 60_000;
            long wake = stage.Now + DeviceNoteDirector.WalkMs;
            stage.Set(pad, false);
            stage.RowsReadAt = 9000;
            stage.Walk(wake);
            Assert.False(stage.Director.Knows(pad));
            // An enumeration that began just after the wake is too early.
            stage.RowsReadAt = wake + 1000;
            stage.Walk(wake + 5000);
            Assert.False(stage.Director.Knows(pad));
            stage.RowsReadAt = wake + 5000;
            stage.Walk(wake + 30_000);
            Assert.True(stage.Director.Knows(pad));
            Assert.Empty(stage.Said);
        }

        /// <summary>A hidden controller's USB port that PadForge cycled
        /// itself: the controller drops and comes back, and neither is
        /// said.</summary>
        [Fact]
        public void APortPadForgeCycled_IsNotNews()
        {
            var stage = new Stage();
            var pad = stage.Add("Hidden Pad", online: true);
            stage.Walk(20_000);

            long cycled = stage.Now;
            stage.Director.Mark(cycled);
            stage.Set(pad, false);
            stage.RowsReadAt = cycled + 1000;
            stage.Walk(cycled + 3000);
            stage.Set(pad, true);
            stage.RowsReadAt = cycled + 3000;
            stage.Walk(cycled + 5000);
            stage.RowsReadAt = cycled + 5000;
            stage.Walk(cycled + 30_000);
            Assert.True(stage.Director.Knows(pad));
            Assert.Empty(stage.Said);
        }

        // ── what is said ───────────────────────────────────────────────

        [Fact]
        public void DevicesThatChangeWithinHalfASecond_ShareOneLine()
        {
            var stage = new Stage();
            var one = stage.Add("One", online: true, slots: 1);
            var two = stage.Add("Two", online: true, slots: 2);
            var three = stage.Add("Three", online: true, slots: 4);
            var four = stage.Add("Four", online: true, slots: 8);
            stage.Walk(1000);

            // A hub goes: two at one walk and one at the next. The fourth
            // follows a second later and has a line of its own.
            stage.Set(one, false);
            stage.Set(two, false);
            stage.Walk(1250);
            stage.Set(three, false);
            stage.Walk(2250);
            stage.Set(four, false);
            stage.Walk(6000);
            Assert.Equal(new[] { "1750 Disconnected x3", "3000 Disconnected Four empties 3" }, stage.Said);
            // The line names the three the hub took (#600 item 42).
            Assert.Equal(new[] { "One,Two,Three" }, stage.Named);

            // The hub comes back with all four, well after the ten seconds.
            stage.Said.Clear();
            stage.Walk(20_000);
            foreach (var device in new[] { one, two, three, four }) stage.Set(device, true);
            stage.Walk(21_000);
            Assert.Equal(new[] { "20750 Connected x4" }, stage.Said);
            Assert.Equal("One,Two,Three,Four", stage.Named.Last());
        }

        /// <summary>The line names the controller a drop left with no
        /// device connected. A row that raises no note of its own, such as
        /// a merged keyboard row, still counts as a device the controller
        /// has.</summary>
        [Fact]
        public void TheLastDeviceOfAController_NamesTheController()
        {
            var stage = new Stage();
            var pad = stage.Add("Pad", online: true, slots: 0b0001);
            var wheel = stage.Add("Wheel", online: true, slots: 0b0011);
            var stick = stage.Add("Stick", online: true, slots: 0b0100);
            stage.Add("All Keyboards (Merged)", online: true, slots: 0b0100, raises: false);
            stage.Walk(1000);

            // The wheel still drives the first controller.
            stage.Set(pad, false);
            stage.Walk(4000);
            // Now the first and the second have nothing: the lower is named.
            stage.Set(wheel, false);
            stage.Walk(8000);
            // The third keeps its keyboard row.
            stage.Set(stick, false);
            stage.Walk(12_000);
            Assert.Equal(new[]
            {
                "1750 Disconnected Pad",
                "4750 Disconnected Wheel empties 0",
                "8750 Disconnected Stick",
            }, stage.Said);
        }

        [Fact]
        public void EmptiedSlot_IsTheLowestControllerLeftWithNoDeviceConnected()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
            var rows = new List<DeviceNoteReading>
            {
                new DeviceNoteReading(a, false, 0b0110, true),
                new DeviceNoteReading(b, true, 0b0010, true),
                new DeviceNoteReading(c, false, 0b0100, false),
            };
            // Slot 1 has b. Slot 2 has only c, which is away too.
            Assert.Equal(2, DeviceNoteDirector.EmptiedSlot(rows, a));
            // With c online the device left nothing empty.
            rows[2] = new DeviceNoteReading(c, true, 0b0100, false);
            Assert.Equal(-1, DeviceNoteDirector.EmptiedSlot(rows, a));
            // A device that is back has left nothing empty, and neither has
            // one that is mapped nowhere.
            rows[0] = new DeviceNoteReading(a, true, 0b0110, true);
            Assert.Equal(-1, DeviceNoteDirector.EmptiedSlot(rows, a));
            Assert.Equal(-1, DeviceNoteDirector.EmptiedSlot(rows, Guid.NewGuid()));
        }

        /// <summary>The rows that are no connections: Twitch Chat, a
        /// microphone's voice phrases, Game Telemetry, a keyboard screen, an
        /// overlay. They come and go and nothing is said.</summary>
        [Fact]
        public void ARowThatIsNoConnection_NeverRaisesANote()
        {
            var stage = new Stage();
            var chat = stage.Add("Twitch Chat", online: true, raises: false);
            stage.Walk(1000);
            for (long at = 5000; at < 400_000; at += 20_000)
            {
                stage.Set(chat, false);
                stage.Walk(at);
                stage.Set(chat, true);
                stage.Walk(at + 10_000);
            }
            Assert.Empty(stage.Said);
            Assert.Empty(stage.Quieted);
            Assert.False(stage.Director.Knows(chat));
        }

        /// <summary>A device taken off its controller, or one whose
        /// controller is deleted or turned off, is no longer mapped: it is
        /// forgotten with the line that was about to name it, and read as
        /// for the first time when it is mapped again.</summary>
        [Fact]
        public void ADeviceThatIsNoLongerMapped_IsForgottenWithItsLine()
        {
            var stage = new Stage();
            var pad = stage.Add("Pad", online: true);
            stage.Walk(1000);
            stage.Set(pad, false);
            stage.Walk(1250);
            var reading = stage.Mapped.Single();
            stage.Remove(pad);
            stage.Walk(10_000);
            Assert.Empty(stage.Said);
            Assert.False(stage.Director.Knows(pad));

            // Mapped again while it is away: nothing to say of that.
            stage.Mapped.Add(reading);
            stage.Walk(20_000);
            Assert.Empty(stage.Said);
            Assert.True(stage.Director.Knows(pad));
        }

        /// <summary>The flyout shows one line at a time. A line waits for
        /// it, and lines of one kind that wait together become one.</summary>
        [Fact]
        public void ALineWaitsForTheFlyout_AndLinesOfOneKindBecomeOne()
        {
            var stage = new Stage();
            var one = stage.Add("One", online: true, slots: 1);
            var two = stage.Add("Two", online: true, slots: 2);
            var three = stage.Add("Three", online: false, slots: 4);
            stage.Walk(1000);

            stage.Free = false;
            stage.Set(one, false);
            stage.Walk(4000);
            stage.Set(two, false);
            stage.Walk(8000);
            stage.Set(three, true);
            stage.Walk(10_000);
            Assert.Empty(stage.Said);

            // One line at each walk the flyout is free.
            stage.Free = true;
            stage.Walk(10_250);
            Assert.Equal(new[] { "10250 Disconnected x2" }, stage.Said);
            // The two lines that became one name both devices.
            Assert.Equal(new[] { "One,Two" }, stage.Named);
            stage.Walk(10_500);
            Assert.Equal(new[] { "10250 Disconnected x2", "10500 Connected Three" }, stage.Said);
            stage.Walk(20_000);
            Assert.Equal(2, stage.Said.Count);
        }

        [Fact]
        public void WithBothNotesOff_NothingIsKept_AndANoteTurnedOnStartsFromWhatIsTrueThen()
        {
            var stage = new Stage { Drops = false, Returns = false };
            var pad = stage.Add("Pad", online: true);
            stage.Walk(5000);
            Assert.False(stage.Director.Knows(pad));
            stage.Set(pad, false);
            stage.Walk(10_000);

            stage.Drops = true;
            stage.Walk(30_000);
            Assert.True(stage.Director.Knows(pad));
            Assert.Empty(stage.Said);
        }

        [Fact]
        public void ALineWhoseNoteWasTurnedOffWhileItWaited_IsNotSaid()
        {
            var stage = new Stage { Free = false };
            var pad = stage.Add("Pad", online: true);
            stage.Walk(1000);
            stage.Set(pad, false);
            stage.Walk(4000);
            stage.Drops = false;
            stage.Free = true;
            stage.Walk(10_000);
            Assert.Empty(stage.Said);
        }

        /// <summary>A device that keeps disconnecting gets its line once,
        /// with one word to the diagnostics log, and the same word with no
        /// line while the Device Disconnected Note is off.</summary>
        [Fact]
        public void ADeviceThatKeepsDisconnecting_GetsOneLineAndOneWordToTheLog()
        {
            foreach (bool drops in new[] { true, false })
            {
                var stage = new Stage { Drops = drops };
                var pad = stage.Add("Pad", online: true);
                stage.Walk(1000);
                for (int i = 0; i < 6; i++)
                {
                    stage.Set(pad, false);
                    stage.Walk(stage.Now + 5000);
                    stage.Set(pad, true);
                    stage.Walk(stage.Now + 15_000);
                }
                Assert.Equal(new[] { "Pad" }, stage.Quieted);
                Assert.Equal(drops ? 1 : 0, stage.Said.Count(s => s.Contains("KeepsDisconnecting", StringComparison.Ordinal)));
                // Nothing after the third drop, which fell at 41 seconds.
                Assert.DoesNotContain(stage.Said, s => long.Parse(s.Split(' ')[0]) > 45_000);
            }
        }

        // ── the note on screen and PadForge's own work ─────────────────

        /// <summary>A drop is said half a second after the walk that reads
        /// it, and while its note shows, the pad's return turns the same
        /// note to "is back" with no new note (#600 item 42).</summary>
        [Fact]
        public void TheNoteOnScreen_TurnsToTheReturnInPlace()
        {
            var stage = new Stage { Screen = true };
            var pad = stage.Add("DualSense", online: true);
            stage.Walk(1000);
            stage.Set(pad, false);
            stage.Walk(2250);
            stage.Set(pad, true);
            stage.Walk(30_000);
            Assert.Equal(new[] { "1750 Disconnected DualSense empties 0", "2500 IsBack DualSense in place" }, stage.Said);

            // Once the note has left, a return waits out the ten seconds and
            // is a note of its own.
            stage.Said.Clear();
            stage.Set(pad, false);
            stage.Walk(30_250);
            stage.Walk(35_000);
            stage.Set(pad, true);
            stage.Walk(45_000);
            Assert.Equal(new[] { "30750 Disconnected DualSense empties 0", "40750 Connected DualSense" }, stage.Said);
        }

        /// <summary>A burst on screen follows its devices: one back leaves
        /// the other named, and both back turn it to the return.</summary>
        [Fact]
        public void ABurstOnScreen_FollowsEachDevice()
        {
            var stage = new Stage { Screen = true };
            var one = stage.Add("DualSense", online: true, slots: 1);
            var two = stage.Add("T.16000M", online: true, slots: 2);
            stage.Walk(1000);
            stage.Set(one, false);
            stage.Set(two, false);
            stage.Walk(2000);
            stage.Set(one, true);
            stage.Walk(2500);
            stage.Set(two, true);
            stage.Walk(20_000);
            Assert.Equal(new[]
            {
                "1750 Disconnected x2", "2250 Disconnected T.16000M empties 1 in place", "2750 IsBack x2 in place",
            }, stage.Said);
            Assert.Equal("DualSense,T.16000M", stage.Named.Last());
        }

        /// <summary>A pad that drops three times while its note shows turns
        /// the note to the count, which grows in place without giving the
        /// note more time.</summary>
        [Fact]
        public void TheCount_ComesInPlace_AndGrows()
        {
            var stage = new Stage { Screen = true };
            var pad = stage.Add("DualSense", online: true);
            stage.Walk(1000);
            foreach (long at in new long[] { 1000, 2000, 3000, 4000 })
            {
                stage.Set(pad, false);
                stage.Walk(at + 500);
                stage.Set(pad, true);
                stage.Walk(at + 1000);
            }
            Assert.Equal(new[]
            {
                // Said though the pad was back a walk before it: the drop
                // was decided, and the next walk turns it to the return.
                "1750 Disconnected DualSense", "2000 IsBack DualSense in place",
                "2250 Disconnected DualSense empties 0 in place", "2750 IsBack DualSense in place",
                "3250 KeepsDisconnecting DualSense times 3 in place",
                "4250 KeepsDisconnecting DualSense times 4 in place",
            }, stage.Said);
            Assert.Equal(new[] { "DualSense" }, stage.Quieted);
        }

        /// <summary>A drop PadForge's own work causes says nothing and
        /// counts nothing, and a pad that never left says nothing when it
        /// is read back.</summary>
        [Fact]
        public void AnOwnWorkDrop_IsNeverNews()
        {
            var stage = new Stage { Screen = true };
            var pad = stage.Add("Xbox Wireless Controller", online: true);
            stage.Walk(10_000);
            for (int i = 0; i < 4; i++)
            {
                long stamped = stage.Now;
                stage.Set(pad, false, own: stamped);
                stage.Walk(stamped + 1500);
                stage.Set(pad, true, own: stamped);
                stage.Walk(stamped + 3000);
            }
            Assert.Empty(stage.Said);
            Assert.Empty(stage.Quieted);
            Assert.Equal(DeviceNoteDirector.Never, stage.Director.StateOf(pad).Drop1);

            // The next drop with no stamp is said.
            stage.Set(pad, false);
            stage.Walk(stage.Now + 1000);
            Assert.Single(stage.Said);
        }

        /// <summary>A device's count of drops that waits behind another note
        /// counts every drop while it waits, and shows the count it reached
        /// when its turn comes.</summary>
        [Fact]
        public void ACountThatWaitsBehindANote_CountsEveryDropWhileItWaits()
        {
            var stage = new Stage();
            var pad = stage.Add("Pad", online: true);
            stage.Walk(1000);
            stage.Set(pad, false);
            stage.Walk(2000);
            stage.Set(pad, true);
            stage.Walk(20_000);
            stage.Set(pad, false);
            stage.Walk(22_000);
            stage.Set(pad, true);
            stage.Walk(40_000);

            // Another device's note is up. The third drop is decided and its
            // line waits, and the pad drops a fourth time while it waits.
            stage.Free = false;
            stage.Set(pad, false);
            stage.Walk(41_000);
            stage.Set(pad, true);
            stage.Walk(42_000);
            stage.Set(pad, false);
            stage.Walk(43_000);
            Assert.Equal(4, stage.Director.StateOf(pad).KeepsCount);

            stage.Free = true;
            stage.Walk(43_250);
            Assert.Equal("43250 KeepsDisconnecting Pad times 4", stage.Said.Last());
        }

        /// <summary>A drop's line that waits behind another note names the
        /// controller it left empty as that controller is when the line
        /// shows: a device that came online on it since leaves it named by
        /// nothing.</summary>
        [Fact]
        public void AWaitingDropLine_NamesTheControllerItEmptiesWhenItShows()
        {
            var stage = new Stage();
            var pad = stage.Add("Pad", online: true, slots: 1);
            var wheel = stage.Add("Wheel", online: false, slots: 1);
            stage.Walk(1000);

            stage.Free = false;
            stage.Set(pad, false);
            stage.Walk(2000);
            stage.Set(wheel, true);
            stage.Walk(3000);
            Assert.Empty(stage.Said);

            stage.Free = true;
            stage.Walk(3250);
            Assert.Equal(new[] { "3250 Disconnected Pad" }, stage.Said);
        }

        /// <summary>A device the user was told is connected before a sleep
        /// that is still away when the rows are believed again says nothing
        /// when it returns: it was connected before the sleep and is
        /// connected after it. An engine stop forgets it.</summary>
        [Fact]
        public void ADeviceLateBackFromASleep_SaysNothing()
        {
            var stage = new Stage { Now = 10_000 };
            var pad = stage.Add("Pad", online: true);
            stage.Walk(20_000);
            Assert.True(stage.Director.Knows(pad));

            // Suspend and resume.
            stage.Director.Mark(20_000);
            stage.Director.Mark(21_000);
            stage.Set(pad, false);
            stage.RowsReadAt = 26_020;
            stage.Walk(27_000);
            Assert.True(stage.Director.Knows(pad));
            stage.Set(pad, true);
            stage.Walk(40_000);
            Assert.Empty(stage.Said);

            // A late return after an engine stop and start is news: the stop
            // forgot what the user was told.
            stage.RowsReadAt = null;
            stage.Director.Reset();
            stage.Director.Mark(stage.Now);
            stage.Set(pad, false);
            stage.RowsReadAt = stage.Now + 6000;
            stage.Walk(stage.Now + 7000);
            stage.Set(pad, true);
            stage.Walk(stage.Now + 2000);
            Assert.Single(stage.Said);
            Assert.Contains("Connected Pad", stage.Said[0], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The words of the device notes (#597). They read the string table, so
    /// they run with the tests that change the language.
    /// </summary>
    [Collection("CultureSwitching")]
    public class DeviceNoteTextTests
    {
        private static string English(string key)
        {
            var resx = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx"));
            return (string)resx.Root.Elements("data").Single(d => (string)d.Attribute("name") == key).Element("value");
        }

        /// <summary>The lines as the issue words them.</summary>
        [Fact]
        public void TheEnglishLines_AreTheIssuesLines()
        {
            Assert.Equal("{0} disconnected", English("Main_DeviceDisconnected_Format"));
            Assert.Equal("{0} disconnected. {1} has no device left.", English("Main_DeviceDisconnectedLast_Format"));
            Assert.Equal("{0} connected", English("Main_DeviceConnected_Format"));
            Assert.Equal("{0} connected, battery {1}%", English("Main_DeviceConnectedBattery_Format"));
            // A burst names the devices it took (#600 item 42).
            Assert.Equal("{0} devices disconnected: {1}", English("Main_DevicesDisconnectedNamed_Format"));
            Assert.Equal("{0} devices connected: {1}", English("Main_DevicesConnectedNamed_Format"));
            // The Notes board's words: the count grows in the same note
            // (#600 item 42).
            Assert.Equal("{0} dropped {1} times in a minute", English("Main_DeviceKeepsDisconnecting_Format"));
            Assert.Equal("{0} is back", English("Main_DeviceIsBack_Format"));
            Assert.Equal("{0} devices are back: {1}", English("Main_DevicesAreBack_Format"));
            Assert.Equal("Device Disconnected Note", English("Dashboard_DeviceDisconnectedNote"));
            Assert.Equal("Device Connected Note", English("Dashboard_DeviceConnectedNote"));
        }

        [Fact]
        public void ALine_TakesTheFormItsNoteCallsFor()
        {
            var s = Strings.Instance;
            var pad = Guid.NewGuid();
            var burst = new[] { "DualSense", "T.16000M", "G29" };
            string Text(Kind kind, int count, string controller = null, int battery = -1, int slot = -1)
                => DeviceNoteDirector.Text(new DeviceNote(kind, count, count == 1 ? pad : Guid.Empty, slot) { Times = 3 }, count == 1 ? "DualSense" : null, controller, battery,
                    count == 1 ? null : burst.Take(count).ToArray());

            Assert.Equal(string.Format(s.Main_DeviceDisconnected_Format, "DualSense"), Text(Kind.Disconnected, 1));
            Assert.Equal(string.Format(s.Main_DeviceDisconnectedLast_Format, "DualSense", "Virtual Controller 2"),
                Text(Kind.Disconnected, 1, controller: "Virtual Controller 2", slot: 1));
            Assert.Equal(string.Format(s.Main_DevicesDisconnectedNamed_Format, 3, "DualSense, T.16000M, G29"), Text(Kind.Disconnected, 3));
            Assert.Equal("3 devices disconnected: DualSense, T.16000M, G29", Text(Kind.Disconnected, 3));

            // The battery form only when the level is known. Zero is a level.
            Assert.Equal(string.Format(s.Main_DeviceConnected_Format, "DualSense"), Text(Kind.Connected, 1));
            Assert.Equal(string.Format(s.Main_DeviceConnectedBattery_Format, "DualSense", 80), Text(Kind.Connected, 1, battery: 80));
            Assert.Equal(string.Format(s.Main_DeviceConnectedBattery_Format, "DualSense", 0), Text(Kind.Connected, 1, battery: 0));
            Assert.Equal(string.Format(s.Main_DevicesConnectedNamed_Format, 2, "DualSense, T.16000M"), Text(Kind.Connected, 2, battery: 80));

            Assert.Equal(string.Format(s.Main_DeviceKeepsDisconnecting_Format, "DualSense", 3), Text(Kind.KeepsDisconnecting, 1));
            Assert.Equal("DualSense dropped 3 times in a minute", Text(Kind.KeepsDisconnecting, 1));
            Assert.Equal(string.Format(s.Main_DeviceIsBack_Format, "DualSense"), Text(Kind.IsBack, 1));
            Assert.Equal(string.Format(s.Main_DevicesAreBack_Format, 2, "DualSense, T.16000M"), Text(Kind.IsBack, 2));
            Assert.Equal(string.Empty, Text(Kind.None, 1));
            Assert.Equal(string.Empty, Text(Kind.Silenced, 1));

            // Every form names the device or the count it was given.
            foreach (string line in new[] { Text(Kind.Disconnected, 1), Text(Kind.Connected, 1, battery: 80), Text(Kind.KeepsDisconnecting, 1) })
                Assert.Contains("DualSense", line, StringComparison.Ordinal);
            Assert.Contains("Virtual Controller 2", Text(Kind.Disconnected, 1, controller: "Virtual Controller 2", slot: 1), StringComparison.Ordinal);
            Assert.Contains("80", Text(Kind.Connected, 1, battery: 80), StringComparison.Ordinal);
            Assert.Contains("3", Text(Kind.Disconnected, 3), StringComparison.Ordinal);
        }

        /// <summary>A burst's names are joined by the language's own list
        /// separator, the ideographic comma in Japanese.</summary>
        [Fact]
        public void ABurstsNames_AreJoinedByTheLanguagesSeparator()
        {
            var previous = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ja");
                string separator = Strings.Instance.Search_WhereSeparator;
                Assert.NotEqual(", ", separator);
                var note = new DeviceNote(Kind.Disconnected, 2, Guid.Empty, -1, new[] { Guid.NewGuid(), Guid.NewGuid() });
                string line = DeviceNoteDirector.Text(note, null, null, -1, new[] { "DualSense", "G29" });
                Assert.Equal(string.Format(Strings.Instance.Main_DevicesDisconnectedNamed_Format, 2, "DualSense" + separator + "G29"), line);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = previous;
            }
        }
    }

    /// <summary>
    /// The device notes (#597) in the app: which rows are mapped and which of
    /// them raise a note, the two switches in the settings file and in a
    /// profile, the Overlays switch that counts them, and the wiring of the
    /// walk, read from the source where no engine runs in a test.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class DeviceNoteWiringTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _savedProfiles = SettingsManager.Profiles;
        private readonly string _savedActiveProfileId = SettingsManager.ActiveProfileId;
        private readonly ProfileData _savedPendingDefault = SettingsManager.PendingDefaultSnapshot;
        private readonly bool[] _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly MappingSet[] _savedMappingSets = SettingsManager.SlotMappingSets;
        private readonly List<int> _savedXboxOrder = SettingsManager.XboxSlotOrder;
        private readonly List<int> _savedPsOrder = SettingsManager.PlayStationSlotOrder;
        private readonly List<int> _savedExtOrder = SettingsManager.ExtendedSlotOrder;
        private readonly List<int> _savedKbmOrder = SettingsManager.KeyboardMouseSlotOrder;
        private readonly List<int> _savedMidiOrder = SettingsManager.MidiSlotOrder;
        private readonly Action _savedAfterRefresh = SettingsService.AfterMappingSetsRefreshed;

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.Profiles = _savedProfiles;
            SettingsManager.ActiveProfileId = _savedActiveProfileId;
            SettingsManager.PendingDefaultSnapshot = _savedPendingDefault;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsManager.XboxSlotOrder = _savedXboxOrder;
            SettingsManager.PlayStationSlotOrder = _savedPsOrder;
            SettingsManager.ExtendedSlotOrder = _savedExtOrder;
            SettingsManager.KeyboardMouseSlotOrder = _savedKbmOrder;
            SettingsManager.MidiSlotOrder = _savedMidiOrder;
            SettingsService.AfterMappingSetsRefreshed = _savedAfterRefresh;
        }

        private const string HidPath = @"\\?\HID#VID_054C&PID_0CE6#8&1a2b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        /// <summary>Controllers 0 and 3 exist and are on, 1 exists and is
        /// off, and the rest do not exist. The real service pair, as
        /// MainWindow builds it.</summary>
        private static (MainViewModel vm, InputService svc) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotCreated[0] = SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotCreated[1] = true;
            SettingsManager.SlotCreated[3] = SettingsManager.SlotEnabled[3] = true;
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.XboxSlotOrder = new List<int> { 0, 1, 3 };
            SettingsManager.PlayStationSlotOrder = new List<int>();
            SettingsManager.ExtendedSlotOrder = new List<int>();
            SettingsManager.KeyboardMouseSlotOrder = new List<int>();
            SettingsManager.MidiSlotOrder = new List<int>();

            var vm = new MainViewModel();
            var svc = new InputService(vm) { SettingsService = new SettingsService(vm) };
            return (vm, svc);
        }

        private static UserDevice Device(string name, string path, bool online, int type = InputDeviceType.Gamepad, params int[] slots)
        {
            var ud = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                InstanceName = name,
                ProductName = name,
                DevicePath = path,
                CapType = type,
                IsOnline = online,
            };
            SettingsManager.UserDevices.Items.Add(ud);
            foreach (int slot in slots)
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = ud.InstanceGuid, MapTo = slot });
            return ud;
        }

        // ── which rows ─────────────────────────────────────────────────

        /// <summary>The rows that raise a note are the ones the Devices page
        /// counts as connected devices. A row PadForge or an extra feeds
        /// raises none, online or not.</summary>
        [Theory]
        [InlineData(HidPath, InputDeviceType.Gamepad, true)]
        [InlineData("XInput#0", InputDeviceType.Gamepad, true)]
        [InlineData(@"\\?\HID#VID_046D&PID_C08B&MI_00#8&4&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}", InputDeviceType.Mouse, true)]
        [InlineData(@"\\?\HID#VID_046D&PID_C33F#8&4&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}", InputDeviceType.Keyboard, true)]
        [InlineData("midi://winmm:Launchpad", InputDeviceType.Midi, true)]
        [InlineData("falcon://0", InputDeviceType.NovintFalcon, true)]
        [InlineData("analogkb://wooting", InputDeviceType.AnalogKeyboard, true)]
        [InlineData("twitch://chat", InputDeviceType.Gamepad, false)]
        [InlineData("mic://capture/0", InputDeviceType.Microphone, false)]
        [InlineData("telemetry://game", InputDeviceType.GameTelemetry, false)]
        [InlineData("keyboardscreen://overlay/1", InputDeviceType.KeyboardScreenKeys, false)]
        [InlineData("overlay://touchpad", InputDeviceType.Touchpad, false)]
        [InlineData("web://xbox360:phone-1", InputDeviceType.Gamepad, false)]
        [InlineData("peer://den/pad", InputDeviceType.Gamepad, false)]
        [InlineData("headtrack://opentrack", InputDeviceType.HeadTracker, false)]
        [InlineData("aggregate://keyboards", InputDeviceType.Keyboard, false)]
        [InlineData("aggregate://mice", InputDeviceType.Mouse, false)]
        [InlineData(@"\\?\HID#VID_046D&PID_C33F&Col02#8&4&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}", InputDeviceType.ConsumerControl, false)]
        public void OnlyARowTheDevicesPageCountsAsADevice_RaisesANote(string path, int type, bool raises)
        {
            foreach (bool online in new[] { true, false })
            {
                var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), InstanceName = "Row", DevicePath = path, CapType = type, IsOnline = online };
                Assert.Equal(raises, InputService.RaisesDeviceNotes(ud));
                // The Devices page's own rule, which Home's count shares.
                Assert.Equal(raises, DeviceRowViewModel.IsCountedDevice(path, PadForge.Common.DeviceTypeGlyph.KeyFor(type)));
            }
            Assert.False(InputService.RaisesDeviceNotes(null));
            Assert.False(InputService.RaisesDeviceNotes(new UserDevice { InstanceGuid = Guid.Empty, DevicePath = HidPath, CapType = InputDeviceType.Gamepad }));
        }

        // ── a device a program adds ───────────────────────────────────

        /// <summary>Walks the director over the mapped rows for
        /// <paramref name="ms"/>, as the interface's timer does, and gives
        /// each note it answers with.</summary>
        private static List<(Kind Kind, Guid Device)> WalkNotes(DeviceNoteDirector director, ref long now, long ms,
            bool drops, bool returns)
        {
            var said = new List<(Kind, Guid)>();
            var read = new List<DeviceNoteReading>();
            UserDevice[] rows = null;
            long until = now + ms;
            while (now < until)
            {
                now += DeviceNoteDirector.WalkMs;
                InputService.ReadMappedDevices(read, ref rows);
                var note = director.Tick(now, now, read, drops, returns, waiting: false, free: true, shown: false);
                if (note.Kind != Kind.None) said.Add((note.Kind, note.Device));
            }
            return said;
        }

        /// <summary>A mapped device a plugin, a script, an assistant, the
        /// Test Controller or a replay adds raises its notes as hardware
        /// does, each under its own switch. One on no controller raises
        /// none, nor does a virtual controller of PadForge's own, and the
        /// Devices page's count leaves both kinds out as before.</summary>
        [Fact]
        public void AMappedProgramDevice_RaisesItsNotes_AsHardwareDoes()
        {
            foreach (var (drops, returns) in new[] { (true, true), (true, false), (false, true) })
            {
                Arrange();
                var pad = Device("Pad Tool Pad", "program://script/0f1e", online: true, InputDeviceType.Gamepad, 0);
                var test = Device("Test Controller 1", "program://builtin/1a2b", online: true, InputDeviceType.Gamepad, 3);
                var loose = Device("Loose Pad", "program://script/2c3d", online: true, InputDeviceType.Gamepad);
                var output = Device("ViGEm Virtual Gamepad", HidPath, online: true, InputDeviceType.Gamepad, 0);
                Assert.True(InputService.RaisesDeviceNotes(pad));
                Assert.True(InputService.RaisesDeviceNotes(test));
                Assert.False(InputService.RaisesDeviceNotes(output));
                Assert.False(DeviceRowViewModel.IsCountedDevice(pad.DevicePath, "Gamepad"));

                var director = new DeviceNoteDirector();
                long now = Environment.TickCount64;
                Assert.Empty(WalkNotes(director, ref now, 7000, drops, returns));

                pad.IsOnline = false;
                loose.IsOnline = false;
                var away = WalkNotes(director, ref now, 2000, drops, returns);
                pad.IsOnline = true;
                loose.IsOnline = true;
                var back = WalkNotes(director, ref now, 12_000, drops, returns);

                Assert.Equal(drops ? new[] { (Kind.Disconnected, pad.InstanceGuid) } : Array.Empty<(Kind, Guid)>(), away);
                Assert.Equal(returns ? new[] { (Kind.Connected, pad.InstanceGuid) } : Array.Empty<(Kind, Guid)>(), back);
            }
        }

        /// <summary>A program device PadForge takes away itself says
        /// nothing: a session it ended (a plugin it stopped or restarted,
        /// Automation turned off, a level lowered), at once or after the
        /// wait, and a built-in plugin's device turned off. One its own
        /// program removes, or whose program ended by itself, is said as an
        /// unplug is.</summary>
        [Fact]
        public void AProgramDevicePadForgeTakesAway_SaysNothing_OneItsProgramRemoves_IsSaid()
        {
            Arrange();
            long clock = 0;
            var registry = new PadForge.Services.Automation.ProgramDevices(() => clock) { IsAssigned = _ => true, ForgetRow = _ => { } };
            try
            {
                PadForge.Services.Automation.AutomationSession Session(int number, string key)
                    => new PadForge.Services.Automation.AutomationSession(null, new MemoryStream(), null, number,
                        System.Threading.CancellationToken.None, () => 0)
                    {
                        Who = new PadForge.Services.Automation.AllowedProgram(key, "Pad Tool", "script", null, null, null,
                            PadForge.Services.Automation.AutomationLevel.ChangeSettings),
                    };
                var shape = new ProgramDeviceShape();
                var stopped = Session(1, @"script|C:\TOOLS\A.EXE");
                var lowered = Session(2, @"script|C:\TOOLS\B.EXE");
                var quit = Session(3, @"script|C:\TOOLS\C.EXE");
                var removes = Session(4, @"script|C:\TOOLS\D.EXE");
                Assert.Null(registry.Declare(stopped, "pad", "Pad", shape, out var a, out _));
                Assert.Null(registry.Declare(lowered, "pad", "Pad", shape, out var b, out _));
                Assert.Null(registry.Declare(quit, "pad", "Pad", shape, out var c, out _));
                Assert.Null(registry.Declare(removes, "pad", "Pad", shape, out var d, out _));
                var builtIn = registry.DeclareBuiltIn("Test Controller", "pad-1", "Test Controller 1", shape);

                UserDevice Row(PadForge.Services.Automation.ProgramDevices.Entry e, int slot)
                {
                    var ud = new UserDevice
                    {
                        InstanceGuid = e.Guid, InstanceName = "Pad " + e.Id, ProductName = "Pad " + e.Id,
                        DevicePath = "program://script/" + e.Id, CapType = InputDeviceType.Gamepad, IsOnline = true,
                    };
                    SettingsManager.UserDevices.Items.Add(ud);
                    SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = e.Guid, MapTo = slot });
                    return ud;
                }
                var rows = new[] { Row(a, 0), Row(b, 0), Row(c, 3), Row(d, 3), Row(builtIn, 3) };

                // The walk's clock starts behind the PC's, so the stamps of
                // PadForge's own work, made on the PC's clock, are read as
                // fresh once it is set to that clock below.
                var director = new DeviceNoteDirector();
                long now = Environment.TickCount64 - 10_000;
                Assert.Empty(WalkNotes(director, ref now, 7000, true, true));

                // PadForge's own: a plugin's run it stopped, after the wait,
                // a level lowered, at once, and a built-in turned off.
                registry.SessionEnded(stopped, now: false, own: true);
                clock += registry.OfflineAfterMs;
                registry.Tick();
                registry.SessionEnded(lowered, now: true, own: true);
                registry.RemoveBuiltIn(builtIn);
                rows[0].IsOnline = rows[1].IsOnline = rows[4].IsOnline = false;
                now = Environment.TickCount64;
                Assert.Empty(WalkNotes(director, ref now, 12_000, true, true));

                // The program's own: it ended, and it removed its device.
                registry.SessionEnded(quit, now: true);
                rows[2].IsOnline = false;
                Assert.Equal(new[] { (Kind.Disconnected, c.Guid) }, WalkNotes(director, ref now, 12_000, true, true));
                Assert.Null(registry.Gone(removes, d.Id));
                rows[3].IsOnline = false;
                Assert.Equal(new[] { (Kind.Disconnected, d.Guid) }, WalkNotes(director, ref now, 12_000, true, true));
            }
            finally
            {
                registry.Dispose();
            }
        }

        /// <summary>A plugin's run PadForge stops or restarts marks its
        /// grant before the process goes, and the session's end reads the
        /// mark, so its devices go as PadForge's own drop.</summary>
        [Fact]
        public void APluginPadForgeStops_EndsItsSessionAsItsOwnDrop()
        {
            string manager = ExtrasPageTests.RepoText("PadForge.App", "Services", "Plugins", "PluginManager.cs").Replace("\r", "");
            string stopRun = Body(manager, "        private void StopRun(PluginRun run)\n");
            int mark = stopRun.IndexOf("if (run.Grant != null) run.Grant.Stopped = true;", StringComparison.Ordinal);
            int stop = stopRun.IndexOf("run.Process.Stop();", StringComparison.Ordinal);
            Assert.True(mark > 0 && stop > mark, "the grant is not marked before the run stops");
            string sessions = ExtrasPageTests.RepoText("PadForge.App", "Services", "Automation", "AutomationSessions.cs");
            Assert.Contains("Devices.SessionEnded(session, now: false, own: session.Grant?.Stopped == true);", sessions);
        }

        /// <summary>Mapped means assigned to a virtual controller that
        /// exists and is on. Each such device is read once, with every
        /// controller of that kind it is on.</summary>
        [Fact]
        public void ReadMappedDevices_ReadsTheDevicesOnControllersThatExistAndAreOn()
        {
            Arrange();
            var pad = Device("Pad", HidPath, online: true, InputDeviceType.Gamepad, 0);
            var wheel = Device("Wheel", HidPath.Replace("0CE6", "C24F"), online: false, InputDeviceType.Driving, 0, 3, 1);
            var chat = Device("Twitch Chat", "twitch://chat", online: true, InputDeviceType.Gamepad, 0);
            Device("On a Controller That Is Off", HidPath.Replace("0CE6", "0001"), online: true, InputDeviceType.Gamepad, 1);
            Device("On No Controller That Exists", HidPath.Replace("0CE6", "0002"), online: true, InputDeviceType.Gamepad, 2);
            Device("Parked", HidPath.Replace("0CE6", "0003"), online: true, InputDeviceType.Gamepad, -1);
            Device("Assigned Nowhere", HidPath.Replace("0CE6", "0004"), online: true);
            // An assignment whose device has no row.
            var ghost = Guid.NewGuid();
            SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = ghost, MapTo = 3 });

            var read = new List<DeviceNoteReading> { new DeviceNoteReading(Guid.NewGuid(), true, 1, true) };
            UserDevice[] rows = null;
            InputService.ReadMappedDevices(read, ref rows);

            Assert.Equal(new[] { pad.InstanceGuid, wheel.InstanceGuid, chat.InstanceGuid, ghost }.OrderBy(g => g), read.Select(r => r.Device).OrderBy(g => g));
            var byDevice = read.ToDictionary(r => r.Device);
            Assert.Equal((true, 0b0001, true), (byDevice[pad.InstanceGuid].Online, byDevice[pad.InstanceGuid].Slots, byDevice[pad.InstanceGuid].RaisesNotes));
            Assert.Equal((false, 0b1001, true), (byDevice[wheel.InstanceGuid].Online, byDevice[wheel.InstanceGuid].Slots, byDevice[wheel.InstanceGuid].RaisesNotes));
            Assert.Equal((true, 0b0001, false), (byDevice[chat.InstanceGuid].Online, byDevice[chat.InstanceGuid].Slots, byDevice[chat.InstanceGuid].RaisesNotes));
            Assert.Equal((false, 0b1000, false), (byDevice[ghost].Online, byDevice[ghost].Slots, byDevice[ghost].RaisesNotes));

            // A controller turned off holds no mapped device.
            SettingsManager.SlotEnabled[0] = false;
            InputService.ReadMappedDevices(read, ref rows);
            Assert.Equal(new[] { wheel.InstanceGuid, ghost }.OrderBy(g => g), read.Select(r => r.Device).OrderBy(g => g));
            Assert.Equal(0b1000, read.Single(r => r.Device == wheel.InstanceGuid).Slots);

            // With no lists there is nothing to read.
            SettingsManager.UserSettings = null;
            InputService.ReadMappedDevices(read, ref rows);
            Assert.Empty(read);
        }

        /// <summary>The line's names: the device as the Devices list shows
        /// it, the emptied controller as the interface names it, and the
        /// battery level when PadForge knows it.</summary>
        [Fact]
        public void TheLine_NamesTheDeviceAsTheDevicesListDoes_TheControllerAndTheBattery()
        {
            var (vm, svc) = Arrange();
            // A path that names no node, so the battery is read from the
            // device's state alone and the test asks the PC nothing.
            var pad = Device("DualSense Wireless Controller", "XInput#7", online: true, InputDeviceType.Gamepad, 0, 3);
            var s = Strings.Instance;

            Assert.Equal(string.Format(s.Main_DeviceDisconnected_Format, pad.ResolvedName),
                svc.DeviceNoteText(new DeviceNote(Kind.Disconnected, 1, pad.InstanceGuid, -1)));
            Assert.False(string.IsNullOrEmpty(vm.Pads[3].SlotLabel));
            Assert.Equal(string.Format(s.Main_DeviceDisconnectedLast_Format, pad.ResolvedName, vm.Pads[3].SlotLabel),
                svc.DeviceNoteText(new DeviceNote(Kind.Disconnected, 1, pad.InstanceGuid, 3)));

            // No level known, then one.
            Assert.Equal(string.Format(s.Main_DeviceConnected_Format, pad.ResolvedName),
                svc.DeviceNoteText(new DeviceNote(Kind.Connected, 1, pad.InstanceGuid, -1)));
            pad.InputState = new CustomInputState { BatteryPercent = 64 };
            Assert.Equal(string.Format(s.Main_DeviceConnectedBattery_Format, pad.ResolvedName, 64),
                svc.DeviceNoteText(new DeviceNote(Kind.Connected, 1, pad.InstanceGuid, -1)));

            // A name the user gave the device is the one the list shows.
            pad.DisplayName = "Player One";
            Assert.Equal(string.Format(s.Main_DeviceKeepsDisconnecting_Format, "Player One", 4),
                svc.DeviceNoteText(new DeviceNote(Kind.KeepsDisconnecting, 1, pad.InstanceGuid, -1) { Times = 4 }));
            Assert.Equal(string.Format(s.Main_DeviceIsBack_Format, "Player One"),
                svc.DeviceNoteText(new DeviceNote(Kind.IsBack, 1, pad.InstanceGuid, -1) { InPlace = true }));

            // A device with no row has no line.
            Assert.Null(svc.DeviceNoteText(new DeviceNote(Kind.Disconnected, 1, Guid.NewGuid(), -1)));

            // A burst names each device it took, as the list does, and
            // leaves out one that has no row (#600 item 42). One left alone
            // is said as its own line, and none leaves no line.
            var stick = Device("T.16000M", "XInput#8", online: false, InputDeviceType.Joystick, 1);
            var gone = Guid.NewGuid();
            Assert.Equal(string.Format(s.Main_DevicesDisconnectedNamed_Format, 2, "Player One, T.16000M"),
                svc.DeviceNoteText(new DeviceNote(Kind.Disconnected, 3, Guid.Empty, -1, new[] { pad.InstanceGuid, gone, stick.InstanceGuid })));
            Assert.Equal(string.Format(s.Main_DevicesConnectedNamed_Format, 2, "T.16000M, Player One"),
                svc.DeviceNoteText(new DeviceNote(Kind.Connected, 2, Guid.Empty, -1, new[] { stick.InstanceGuid, pad.InstanceGuid })));
            Assert.Equal(string.Format(s.Main_DeviceDisconnected_Format, "T.16000M"),
                svc.DeviceNoteText(new DeviceNote(Kind.Disconnected, 2, Guid.Empty, -1, new[] { gone, stick.InstanceGuid })));
            Assert.Null(svc.DeviceNoteText(new DeviceNote(Kind.Disconnected, 2, Guid.Empty, -1, new[] { gone, Guid.NewGuid() })));
        }

        // ── the two switches ───────────────────────────────────────────

        [Fact]
        public void BothNotesShipOff_InTheAppTheSettingsFileAndAProfile()
        {
            var vm = new DashboardViewModel();
            Assert.False(vm.EnableDeviceDisconnectedNote);
            Assert.False(vm.EnableDeviceConnectedNote);
            Assert.False(new AppSettingsData().EnableDeviceDisconnectedNote);
            Assert.False(new AppSettingsData().EnableDeviceConnectedNote);
            Assert.False(new ProfileData().EnableDeviceDisconnectedNote);
            Assert.False(new ProfileData().EnableDeviceConnectedNote);

            // The reset puts each back to off, and tells the page so.
            vm.EnableDeviceDisconnectedNote = true;
            vm.EnableDeviceConnectedNote = true;
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.ResetSettingCommand.Execute(nameof(DashboardViewModel.EnableDeviceDisconnectedNote));
            Assert.False(vm.EnableDeviceDisconnectedNote);
            Assert.True(vm.EnableDeviceConnectedNote);
            vm.ResetSettingCommand.Execute(nameof(DashboardViewModel.EnableDeviceConnectedNote));
            Assert.False(vm.EnableDeviceConnectedNote);
            Assert.Contains(nameof(DashboardViewModel.EnableDeviceDisconnectedNote), raised);
            Assert.Contains(nameof(DashboardViewModel.EnableDeviceConnectedNote), raised);
            Assert.True(DashboardViewModel.CanResetSetting(nameof(DashboardViewModel.EnableDeviceDisconnectedNote)));
            Assert.True(DashboardViewModel.CanResetSetting(nameof(DashboardViewModel.EnableDeviceConnectedNote)));
        }

        /// <summary>Each switch round-trips through the settings file, for
        /// the app and inside a profile, and a file from before the notes
        /// reads both off.</summary>
        [Fact]
        public void TheSettingsFile_KeepsBothNotes_ForTheAppAndPerProfile()
        {
            var app = new XmlSerializer(typeof(AppSettingsData));
            foreach (var (drops, returns) in new[] { (true, false), (false, true), (true, true), (false, false) })
            {
                var written = new StringWriter();
                app.Serialize(written, new AppSettingsData { EnableDeviceDisconnectedNote = drops, EnableDeviceConnectedNote = returns });
                var back = (AppSettingsData)app.Deserialize(new StringReader(written.ToString()));
                Assert.Equal((drops, returns), (back.EnableDeviceDisconnectedNote, back.EnableDeviceConnectedNote));
            }

            var profile = new XmlSerializer(typeof(ProfileData));
            foreach (var (drops, returns) in new[] { (true, false), (false, true) })
            {
                var written = new StringWriter();
                profile.Serialize(written, new ProfileData { Id = "p", Name = "Game", EnableDeviceDisconnectedNote = drops, EnableDeviceConnectedNote = returns });
                var back = (ProfileData)profile.Deserialize(new StringReader(written.ToString()));
                Assert.Equal((drops, returns), (back.EnableDeviceDisconnectedNote, back.EnableDeviceConnectedNote));
            }

            // A file from before the notes: neither element, both off, and
            // the three overlays as they were.
            var written3 = new StringWriter();
            profile.Serialize(written3, new ProfileData { Id = "old", Name = "Old", EnableDeviceDisconnectedNote = true, EnableDeviceConnectedNote = true });
            string old = Regex.Replace(written3.ToString(), @"\s*<EnableDevice(Disconnected|Connected)Note>true</EnableDevice(Disconnected|Connected)Note>", "");
            Assert.DoesNotContain("DeviceDisconnectedNote", old, StringComparison.Ordinal);
            Assert.DoesNotContain("DeviceConnectedNote", old, StringComparison.Ordinal);
            var before = (ProfileData)profile.Deserialize(new StringReader(old));
            Assert.False(before.EnableDeviceDisconnectedNote || before.EnableDeviceConnectedNote);
            Assert.True(before.EnableMenuOverlay && before.EnableShiftLayerFlyout && before.EnableProfileOverlay);
        }

        /// <summary>The load, the save, the profile's three mirrors and the
        /// mark that the file changed. A field one of them drops is the
        /// failure a round trip of the data class alone cannot see.</summary>
        [Fact]
        public void EveryLegOfTheSettingsPipeline_CarriesBothNotes()
        {
            string settings = ExtrasPageTests.RepoText("PadForge.App", "Services", "SettingsService.cs");
            string input = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            foreach (string name in new[] { "EnableDeviceDisconnectedNote", "EnableDeviceConnectedNote" })
            {
                // The app's part: load and save.
                Assert.Contains("_mainVm.Dashboard." + name + " = appSettings." + name + ";", settings, StringComparison.Ordinal);
                Assert.Contains("                " + name + " = _mainVm.Dashboard." + name + ",", settings, StringComparison.Ordinal);
                // A profile's part: the active profile at load, and the
                // three mirrors (UpdateActiveProfileSnapshot,
                // SnapshotCurrentProfile, SaveActiveProfileState) with the
                // apply.
                Assert.Contains("_mainVm.Dashboard." + name + " = active." + name + ";", settings, StringComparison.Ordinal);
                Assert.Contains("profile." + name + " = _mainVm.Dashboard." + name + ";", settings, StringComparison.Ordinal);
                Assert.Contains("                " + name + " = _mainVm.Dashboard." + name + ",", input, StringComparison.Ordinal);
                Assert.Contains("profile." + name + " = snapshot." + name + ";", input, StringComparison.Ordinal);
                Assert.Contains("_mainVm.Dashboard." + name + " = profile." + name + ";", input, StringComparison.Ordinal);
                // A change marks the file to be saved.
                Assert.Contains("or nameof(DashboardViewModel." + name + ")", window, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void AProfileSwitch_CarriesBothNotes()
        {
            var (vm, svc) = Arrange();
            var profile = new ProfileData { Id = "p1", Name = "Game A" };
            SettingsManager.Profiles.Add(profile);
            SettingsManager.ActiveProfileId = "p1";

            vm.Dashboard.EnableDeviceDisconnectedNote = true;
            vm.Dashboard.EnableDeviceConnectedNote = false;
            svc.SaveActiveProfileState();
            Assert.True(profile.EnableDeviceDisconnectedNote);
            Assert.False(profile.EnableDeviceConnectedNote);

            var incoming = new ProfileData
            {
                Id = "p2", Name = "Game B",
                SlotCreated = (bool[])SettingsManager.SlotCreated.Clone(),
                SlotEnabled = (bool[])SettingsManager.SlotEnabled.Clone(),
                EnableDeviceDisconnectedNote = false,
                EnableDeviceConnectedNote = true,
            };
            svc.ApplyProfile(incoming);
            Assert.False(vm.Dashboard.EnableDeviceDisconnectedNote);
            Assert.True(vm.Dashboard.EnableDeviceConnectedNote);
        }

        /// <summary>A profile switch forgets what the notes knew, so the
        /// devices the new profile maps are read as for the first
        /// time.</summary>
        [Fact]
        public void AProfileSwitch_ForgetsWhatTheNotesKnew()
        {
            var (_, svc) = Arrange();
            var pad = Device("Pad", HidPath, online: true, InputDeviceType.Gamepad, 0);
            var read = new List<DeviceNoteReading>();
            UserDevice[] rows = null;
            InputService.ReadMappedDevices(read, ref rows);
            svc.DeviceNotes.Tick(1000, 1000, read, noteDrops: true, noteReturns: true, waiting: false, free: true);
            Assert.True(svc.DeviceNotes.Knows(pad.InstanceGuid));

            svc.ApplyProfile(new ProfileData
            {
                Id = "p2", Name = "Game B",
                SlotCreated = (bool[])SettingsManager.SlotCreated.Clone(),
                SlotEnabled = (bool[])SettingsManager.SlotEnabled.Clone(),
            });
            Assert.False(svc.DeviceNotes.Knows(pad.InstanceGuid));
        }

        // ── the Overlays switch ────────────────────────────────────────

        /// <summary>The Overlays switch counts the two notes: it is on while
        /// any of the five rows is, and it brings back the rows it turned
        /// off. The first time, with nothing remembered, it turns on the
        /// three overlays and neither note.</summary>
        [Fact]
        public void TheOverlaysSwitch_CountsTheNotes_AndStillStartsWithTheThreeOverlays()
        {
            // The controllers on or off note (#600 item 42) starts on, as
            // the three overlays do.
            Assert.Equal(39, DashboardViewModel.OverlaysStartingSet);
            Assert.Equal(63, DashboardViewModel.OverlaysAllBits);
            Assert.Equal(8, DashboardViewModel.DeviceDisconnectedNoteBit);
            Assert.Equal(16, DashboardViewModel.DeviceConnectedNoteBit);
            Assert.Equal(32, DashboardViewModel.ControllersNoteBit);

            // On with one note alone.
            var vm = new DashboardViewModel { EnableMenuOverlay = false, EnableShiftLayerFlyout = false, EnableProfileOverlay = false, EnableControllersNote = false };
            Assert.False(vm.OverlaysAnyEnabled);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.EnableDeviceConnectedNote = true;
            Assert.True(vm.OverlaysAnyEnabled);
            Assert.Contains(nameof(DashboardViewModel.OverlaysAnyEnabled), raised);
            // Home's count of the extras in use follows it.
            Assert.Contains(nameof(DashboardViewModel.ExtrasInUseCount), raised);

            // Off remembers the note, and on brings back that row alone.
            vm.OverlaysAnyEnabled = false;
            Assert.False(vm.EnableDeviceConnectedNote);
            Assert.Equal(DashboardViewModel.DeviceConnectedNoteBit, vm.OverlaysLastShown);
            vm.OverlaysAnyEnabled = true;
            Assert.True(vm.EnableDeviceConnectedNote);
            Assert.False(vm.EnableDeviceDisconnectedNote || vm.EnableMenuOverlay || vm.EnableShiftLayerFlyout || vm.EnableProfileOverlay || vm.EnableControllersNote);

            // All six, off and on again.
            vm.EnableMenuOverlay = vm.EnableShiftLayerFlyout = vm.EnableProfileOverlay = vm.EnableDeviceDisconnectedNote = vm.EnableControllersNote = true;
            vm.OverlaysAnyEnabled = false;
            Assert.Equal(DashboardViewModel.OverlaysAllBits, vm.OverlaysLastShown);
            Assert.False(vm.EnableDeviceDisconnectedNote || vm.EnableDeviceConnectedNote);
            vm.OverlaysAnyEnabled = true;
            Assert.True(vm.EnableMenuOverlay && vm.EnableShiftLayerFlyout && vm.EnableProfileOverlay
                && vm.EnableDeviceDisconnectedNote && vm.EnableDeviceConnectedNote && vm.EnableControllersNote);

            // With nothing remembered: the three overlays and the
            // controllers note, and no device note.
            var fresh = new DashboardViewModel { EnableMenuOverlay = false, EnableShiftLayerFlyout = false, EnableProfileOverlay = false, EnableControllersNote = false };
            Assert.Equal(0, fresh.OverlaysLastShown);
            fresh.OverlaysAnyEnabled = true;
            Assert.True(fresh.EnableMenuOverlay && fresh.EnableShiftLayerFlyout && fresh.EnableProfileOverlay && fresh.EnableControllersNote);
            Assert.False(fresh.EnableDeviceDisconnectedNote || fresh.EnableDeviceConnectedNote);

            // What is remembered is held to the six rows.
            fresh.OverlaysLastShown = 0xFF;
            Assert.Equal(63, fresh.OverlaysLastShown);

            // The reset beside the switch: each row at its default.
            vm.EnableControllersNote = false;
            vm.ResetOverlaysCommand.Execute(null);
            Assert.True(vm.EnableMenuOverlay && vm.EnableShiftLayerFlyout && vm.EnableProfileOverlay && vm.EnableControllersNote);
            Assert.False(vm.EnableDeviceDisconnectedNote || vm.EnableDeviceConnectedNote);
        }

        // ── the page ───────────────────────────────────────────────────

        /// <summary>What Shows holds five rows in order, each a preview, a
        /// name over a line on what it shows, a switch and a reset. The two
        /// notes come after Profile Switch Overlay.</summary>
        [Fact]
        public void WhatShows_HasFiveRowsInOrder_EachWithItsPreviewSwitchAndReset()
        {
            const string S = ", Source={x:Static strings:Strings.Instance}}";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var doc = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml"));
            var page = doc.Descendants().Single(e => (string)e.Attribute(x + "Name") == "OverlaysPage");
            var card = page.Elements().Single(e => e.Name.LocalName == "Border" && (string)e.Attribute("Style") == "{StaticResource ExtraCard}");
            Assert.Equal("{Binding Extras_Overlays_WhatShows" + S,
                (string)card.Descendants().First(e => (string)e.Attribute("Style") == "{StaticResource ExtraCardTitle}").Attribute("Text"));
            var rows = card.Elements().Single().Elements().Where(e => e.Name.LocalName == "SettingRow").ToList();

            var expected = new[]
            {
                ("OverlaysPreviewMenu", "Dashboard_MenuOverlay", "Extras_Overlays_MenuHint", "EnableMenuOverlay"),
                ("OverlaysPreviewShift", "Dashboard_ShiftLayerFlyout", "Extras_Overlays_ShiftLayerHint", "EnableShiftLayerFlyout"),
                ("OverlaysPreviewProfile", "Dashboard_ProfileOverlay", "Extras_Overlays_ProfileHint", "EnableProfileOverlay"),
                ("OverlaysPreviewControllers", "Dashboard_ControllersNote", "Extras_Overlays_ControllersNoteHint", "EnableControllersNote"),
                ("OverlaysPreviewDeviceDisconnected", "Dashboard_DeviceDisconnectedNote", "Extras_Overlays_DeviceDisconnectedHint", "EnableDeviceDisconnectedNote"),
                ("OverlaysPreviewDeviceConnected", "Dashboard_DeviceConnectedNote", "Extras_Overlays_DeviceConnectedHint", "EnableDeviceConnectedNote"),
            };
            Assert.Equal(expected.Length, rows.Count);
            var resources = doc.Root.Elements().Single(e => e.Name.LocalName == "UserControl.Resources");
            for (int i = 0; i < rows.Count; i++)
            {
                var (preview, name, hint, property) = expected[i];
                var parts = rows[i].Elements().ToList();
                Assert.Equal(new[] { "Border", "StackPanel", "CheckBox", "SettingResetButton" }, parts.Select(p => p.Name.LocalName));
                Assert.Equal("64", (string)rows[i].Attribute("MinHeight"));

                // The preview in its well.
                Assert.Equal("{StaticResource OverlaysPreviewWell}", (string)parts[0].Attribute("Style"));
                var image = parts[0].Elements().Single();
                Assert.Equal("DecorativeImage", image.Name.LocalName);
                Assert.Equal("{StaticResource " + preview + "}", (string)image.Attribute("Source"));
                // Drawn on the board's 84 by 48 grid, as all five are.
                var drawing = resources.Elements().Single(e => (string)e.Attribute(x + "Key") == preview);
                Assert.Equal("DrawingImage", drawing.Name.LocalName);
                Assert.Equal("M0,0 H84 V48 H0 Z", (string)drawing.Descendants().First(e => e.Name.LocalName == "GeometryDrawing").Attribute("Geometry"));

                // The name over one line on what it shows.
                var texts = parts[1].Elements().ToList();
                Assert.Equal("True", parts[1].Attributes().Single(a => a.Name.LocalName == "SettingRow.Fills").Value);
                Assert.Equal(new[] { "{Binding " + name + S, "{Binding " + hint + S }, texts.Select(t => (string)t.Attribute("Text")));
                Assert.Equal("{StaticResource ExtraSmall}", (string)texts[1].Attribute("Style"));

                // The switch, named for a screen reader by the row's name.
                Assert.Equal("{StaticResource SwitchCheckBox}", (string)parts[2].Attribute("Style"));
                Assert.Equal("{Binding " + name + S, (string)parts[2].Attribute("Content"));
                Assert.Equal("{Binding " + property + "}", (string)parts[2].Attribute("IsChecked"));

                // Its own reset.
                Assert.Equal("{Binding ResetSettingCommand}", (string)parts[3].Attribute("Command"));
                Assert.Equal(property, (string)parts[3].Attribute("CommandParameter"));
                Assert.Equal("{Binding " + name + S, (string)parts[3].Attribute("SettingLabel"));
                Assert.StartsWith("Reset_DashboardViewModel_" + property + "_", (string)parts[3].Attribute("AutomationProperties.AutomationId"));
            }

            // The two notes are drawn in the profile flyout's pill, which
            // is the window they show in.
            string Pill(string key) => resources.Elements().Single(e => (string)e.Attribute(x + "Key") == key)
                .Descendants().First(e => e.Name.LocalName == "RectangleGeometry").Attribute("Rect").Value;
            Assert.Equal(Pill("OverlaysPreviewProfile"), Pill("OverlaysPreviewDeviceDisconnected"));
            Assert.Equal(Pill("OverlaysPreviewProfile"), Pill("OverlaysPreviewDeviceConnected"));
            Assert.Equal(Pill("OverlaysPreviewProfile"), Pill("OverlaysPreviewControllers"));
        }

        /// <summary>Search finds both settings on the Overlays page, each
        /// a switch named by its row, as it finds the row they stand
        /// under.</summary>
        [Fact]
        public void Search_FindsBothNotes()
        {
            var markup = SearchSources.FileNames.ToDictionary(p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));
            var entries = SearchIndexBuilder.Read(markup, PadPage.TabOfGroupName).Entries;
            // The control: the sibling the two are built like.
            var sibling = Assert.Single(entries, e => e.Kind == SearchKind.Setting && e.Label.Key == "Dashboard_ProfileOverlay" && e.ExtraKey == "Overlays");
            Assert.Equal("EnableProfileOverlay", sibling.Value?.Path);
            int order = sibling.Order;
            foreach (var (label, property) in new[]
            {
                ("Dashboard_DeviceDisconnectedNote", "EnableDeviceDisconnectedNote"),
                ("Dashboard_DeviceConnectedNote", "EnableDeviceConnectedNote"),
            })
            {
                var entry = Assert.Single(entries, e => e.Kind == SearchKind.Setting && e.Label.Key == label);
                Assert.Equal(SearchPage.Extras, entry.Page);
                Assert.Equal("Overlays", entry.ExtraKey);
                Assert.Equal(property, entry.Value?.Path);
                Assert.Equal(SearchValueKind.Switch, entry.Value?.Kind);
                Assert.Equal(SearchLabelSource.Toggle, entry.LabelSource);
                Assert.False(entry.UnderMore);
                // Placed as its sibling is, and after it on the page.
                Assert.Equal(sibling.LabelSource, entry.LabelSource);
                Assert.Equal(sibling.CardTitle, entry.CardTitle);
                Assert.Equal(sibling.ScopeName, entry.ScopeName);
                Assert.True(entry.Order > order, label + " does not come after the row before it");
                order = entry.Order;
            }
        }

        // ── the wiring ─────────────────────────────────────────────────

        private static string Body(string source, string head)
        {
            source = source.Replace("\r", "");
            int at = source.IndexOf(head, StringComparison.Ordinal);
            Assert.True(at >= 0, "no " + head);
            int end = source.IndexOf("\n        }\n", at, StringComparison.Ordinal);
            Assert.True(end > at, head + " does not end");
            return source.Substring(at, end - at);
        }

        /// <summary>No engine runs in a test, so the walk's place in the
        /// engine's life is read from the source: it starts over with the
        /// engine, ends with it, runs on the UI timer ahead of the layer
        /// flyout, and marks a sleep and a port PadForge cycled.</summary>
        [Fact]
        public void TheWalk_StartsWithTheEngine_EndsWithIt_AndRunsAheadOfTheLayerFlyout()
        {
            string service = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs").Replace("\r", "");
            string notes = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.DeviceNotes.cs").Replace("\r", "");

            // Start, before the engine's thread does.
            string start = Body(service, "        public void Start()\n");
            int begin = start.IndexOf("StartDeviceNotes();", StringComparison.Ordinal);
            int engine = start.IndexOf("_inputManager.Start();", StringComparison.Ordinal);
            Assert.True(begin > 0 && engine > begin, "the notes are not started over before the engine's thread starts");
            string started = Body(notes, "        private void StartDeviceNotes()\n");
            Assert.Contains("_deviceNotes.Mark(Environment.TickCount64);", started);
            Assert.Contains("SystemEvents.PowerModeChanged += OnDeviceNotePowerMode;", started);

            // Stop, with the window the notes show on.
            string stop = Body(service, "        public void Stop()\n");
            int closed = stop.IndexOf("_switchOverlay = null;", StringComparison.Ordinal);
            int ended = stop.IndexOf("StopDeviceNotes();", StringComparison.Ordinal);
            Assert.True(closed > 0 && ended > closed);
            string stopped = Body(notes, "        private void StopDeviceNotes()\n");
            Assert.Contains("SystemEvents.PowerModeChanged -= OnDeviceNotePowerMode;", stopped);
            Assert.Contains("_deviceNotes.Reset();", stopped);

            // Sleep, at both ends, on the interface's thread.
            string power = Body(notes, "        private void OnDeviceNotePowerMode(object sender, PowerModeChangedEventArgs e)\n");
            Assert.Contains("if (e.Mode != PowerModes.Suspend && e.Mode != PowerModes.Resume) return;", power);
            Assert.Contains("_dispatcher.BeginInvoke(new Action(() => _deviceNotes.Mark(Environment.TickCount64)));", power);

            // The tick: the notes, then the layer flyout, which gives a
            // note its place.
            string tick = Body(service, "        private void UiTimer_Tick(object sender, EventArgs e)\n");
            int walk = tick.IndexOf("UpdateDeviceNotes(_mainVm.Dashboard.EnableDeviceDisconnectedNote, _mainVm.Dashboard.EnableDeviceConnectedNote);", StringComparison.Ordinal);
            int layer = tick.IndexOf("UpdateShiftLayerFlyout();", StringComparison.Ordinal);
            Assert.True(walk > 0 && layer > walk, "the notes do not run ahead of the layer flyout");
            string flyout = Body(service, "        private void UpdateShiftLayerFlyout()\n");
            Assert.Contains("if (!_mainVm.Dashboard.EnableShiftLayerFlyout || sets == null || DeviceNoteIsUp)", flyout);
            Assert.Contains("private bool DeviceNoteIsUp => _switchOverlay != null && _switchOverlay.IsShowingDeviceNote;", notes);

            // The walk itself: four times a second, a port PadForge cycled
            // and its other settling work marked before the rows are read,
            // the engine's own word on when its rows were read, the
            // flyout's word on what it shows, and a line in place changing
            // the note on screen without showing it again (#600 item 42).
            string update = Body(notes, "        private void UpdateDeviceNotes(bool noteDrops, bool noteReturns)\n");
            Assert.Contains("if (now - _lastDeviceNoteWalk < DeviceNoteDirector.WalkMs) return;", update);
            Assert.Contains("long cycled = _controllerRelease.LastCycleAt;", update);
            Assert.Contains("_deviceNotes.Mark(cycled);", update);
            int settled = update.IndexOf("long settled = OwnDrops.SettleAt;", StringComparison.Ordinal);
            int read = update.IndexOf("ReadMappedDevices(_deviceNoteReadings, ref _deviceNoteRows);", StringComparison.Ordinal);
            Assert.True(settled > 0 && read > settled, "a settle is not marked before the rows are read");
            Assert.Contains("_deviceNotes.Mark(settled);", update);
            Assert.Contains("_inputManager?.DevicesEnumeratedSince ?? DeviceNoteDirector.Never,", update);
            Assert.Contains("bool shown = up && _switchOverlay.IsShowingDeviceNote;", update);
            Assert.Contains("waiting: up && !shown, free: !up, shown: shown);", update);
            int inPlace = update.IndexOf("SwitchOverlay().UpdateDeviceNote(text, note.Kind == DeviceNoteKind.IsBack, restartTime: !note.Grows);", StringComparison.Ordinal);
            int shows = update.IndexOf("SwitchOverlay().ShowDeviceNote(text, note.Kind == DeviceNoteKind.Connected);", StringComparison.Ordinal);
            Assert.True(inPlace > 0 && shows > inPlace, "a line in place is not handed to the note on screen");
            Assert.Contains("OwnDrops.EveryAt, OwnDrops.RowAt(ud.InstanceGuid));", notes);

            // A profile switch, and the one line to the diagnostics log.
            Assert.Contains("_deviceNotes.Reset();", Body(service, "        public void ApplyProfile(ProfileData profile)\n"));
            Assert.Contains("_deviceNotes.Silenced = LogDeviceNoteSilence;", service);
            Assert.Contains("PadForge.Engine.SdlDiagLog.WriteLine(", Body(notes, "        private void LogDeviceNoteSilence(Guid device, int drops)\n"));
        }

        /// <summary>The engine says when the enumeration its rows come from
        /// began, for the run that is current, and -1 while it has listed
        /// nothing. Step 1 notes the start before it notes the
        /// enumeration.</summary>
        [Fact]
        public void TheEngineSaysWhenItsRowsWereRead()
        {
            var im = new InputManager();
            var running = typeof(InputManager).GetField("_running",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(running);
            Assert.Equal(-1, im.DevicesEnumeratedSince);
            try
            {
                int run = im.StampRun();
                running.SetValue(im, true);
                im.NoteEnumerationBegan(run, 5000);
                // Begun, and not yet finished.
                Assert.Equal(-1, im.DevicesEnumeratedSince);
                im.NoteDevicesEnumerated(run);
                Assert.Equal(5000, im.DevicesEnumeratedSince);

                // A thread that is no poll loop, and the loop of an earlier
                // run, move nothing.
                im.NoteEnumerationBegan(0, 9000);
                im.NoteEnumerationBegan(run - 1, 9000);
                Assert.Equal(5000, im.DevicesEnumeratedSince);
                im.NoteEnumerationBegan(run, 7000);
                im.NoteDevicesEnumerated(run);
                Assert.Equal(7000, im.DevicesEnumeratedSince);

                // The next run has read nothing yet.
                int next = im.StampRun();
                Assert.Equal(-1, im.DevicesEnumeratedSince);
                im.NoteEnumerationBegan(next, 12_000);
                im.NoteDevicesEnumerated(next);
                Assert.Equal(12_000, im.DevicesEnumeratedSince);

                // Stopped, nothing is listed.
                running.SetValue(im, false);
                Assert.Equal(-1, im.DevicesEnumeratedSince);
            }
            finally
            {
                running.SetValue(im, false);
            }

            string step1 = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs");
            int update = step1.IndexOf("private void UpdateDevices()", StringComparison.Ordinal);
            int began = step1.IndexOf("long enumerationBegan = Environment.TickCount64;", update, StringComparison.Ordinal);
            int sdl = step1.IndexOf("uint[] joystickIds = SDL_GetJoysticks();", update, StringComparison.Ordinal);
            int noteBegan = step1.IndexOf("NoteEnumerationBegan(t_runStamp, enumerationBegan);", update, StringComparison.Ordinal);
            int note = step1.IndexOf("NoteDevicesEnumerated(t_runStamp);", update, StringComparison.Ordinal);
            Assert.True(update > 0 && began > update && sdl > began && noteBegan > sdl && note > noteBegan,
                "Step 1 does not take the start before it reads and note it before the enumeration");
        }
    }

    /// <summary>
    /// The device notes on screen (#597): the Overlays page laid out with its
    /// five previews, and the flyout's window showing a note. Both need the
    /// app's resources.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class DeviceNoteScreenTests
    {
        private static IEnumerable<T> Under<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T hit) yield return hit;
                foreach (var found in Under<T>(child)) yield return found;
            }
        }

        private static void Pump(Dispatcher dispatcher, int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background, (_, _) => frame.Continue = false, dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        /// <summary>The Overlays page draws five rows, one under another in
        /// order, each with its preview drawn in a well of 84 by 48 and its
        /// switch. Both notes read off on a new settings file.</summary>
        [Fact]
        public void TheOverlaysPage_DrawsSixRowsInOrder_WithTheirPreviews()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var dash = new DashboardViewModel
                {
                    DiscoverOpenXrRuntimes = () => Array.Empty<PadForge.Engine.Common.OpenXr.OpenXrRuntimeEntry>(),
                };
                var page = new ExtrasPage { DataContext = dash };
                var ground = new Border { Child = page };
                try
                {
                    Assert.True(page.Show("Overlays"));
                    void Lay()
                    {
                        PageRenderDump.Flush(ground);
                        ground.Measure(new Size(1031, 900));
                        ground.Arrange(new Rect(0, 0, 1031, 900));
                        ground.UpdateLayout();
                    }
                    Lay();
                    PageRenderDump.Settle(dispatcher);
                    Lay();

                    var panel = (FrameworkElement)page.FindName("OverlaysPage");
                    var rows = Under<SettingRow>(panel).ToList();
                    Assert.Equal(6, rows.Count);

                    var expected = new[]
                    {
                        ("OverlaysPreviewMenu", Strings.Instance.Dashboard_MenuOverlay, true),
                        ("OverlaysPreviewShift", Strings.Instance.Dashboard_ShiftLayerFlyout, true),
                        ("OverlaysPreviewProfile", Strings.Instance.Dashboard_ProfileOverlay, true),
                        ("OverlaysPreviewControllers", Strings.Instance.Dashboard_ControllersNote, true),
                        ("OverlaysPreviewDeviceDisconnected", Strings.Instance.Dashboard_DeviceDisconnectedNote, false),
                        ("OverlaysPreviewDeviceConnected", Strings.Instance.Dashboard_DeviceConnectedNote, false),
                    };
                    double last = double.NegativeInfinity;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var (preview, name, on) = expected[i];
                        var row = rows[i];
                        double top = row.TransformToAncestor(ground).Transform(new Point(0, 0)).Y;
                        Assert.True(top > last, name + " is not under the row before it");
                        last = top;
                        Assert.True(row.ActualHeight >= 64, name + " is " + row.ActualHeight + " tall");

                        // Its preview: the page's own drawing, in the well.
                        var image = Assert.Single(Under<DecorativeImage>(row));
                        Assert.Same(page.FindResource(preview), image.Source);
                        Assert.IsType<DrawingImage>(image.Source);
                        var well = (Border)VisualTreeHelper.GetParent(image);
                        Assert.Equal(new Size(84, 48), new Size(well.ActualWidth, well.ActualHeight));
                        Assert.True(image.ActualWidth > 0 && image.ActualHeight > 0, name + "'s preview takes no room");

                        // Its name, and its switch as the settings file starts.
                        Assert.Contains(Under<TextBlock>(row), t => t.Text == name);
                        var toggle = Assert.Single(Under<CheckBox>(row));
                        Assert.Equal(name, toggle.Content);
                        Assert.Equal(on, toggle.IsChecked);
                    }
                    // The six previews are six drawings.
                    Assert.Equal(6, rows.Select(r => Under<DecorativeImage>(r).Single().Source).Distinct().Count());

                    // A switch on the page writes the setting.
                    Under<CheckBox>(rows[4]).Single().IsChecked = true;
                    Assert.True(dash.EnableDeviceDisconnectedNote);
                    Assert.False(dash.EnableDeviceConnectedNote);
                }
                finally
                {
                    ground.Child = null;
                }
            }, TimeSpan.FromMinutes(2), "The Overlays page test timed out.");
        }

        /// <summary>The flyout shows a drop for four seconds and any other
        /// note for two (#600 item 42), and is free
        /// again once it has slid away. A state shown while the note slides
        /// away keeps the window: the end of the old slide, which still
        /// arrives, hides nothing. The window is moved off every screen as
        /// soon as it is shown, before it is drawn.</summary>
        [Fact]
        public void TheFlyout_ShowsADropForFourSecondsAndAReturnForTwo_AndAStateShownWhileItSlidesAwayKeepsTheWindow()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var flyout = new ProfileSwitchOverlay();
                try
                {
                    void OffScreen()
                    {
                        flyout.Left = -32000;
                        flyout.Top = -32000;
                    }
                    var slide = (TranslateTransform)flyout.FlyoutPanel.RenderTransform;
                    // Waits, a few milliseconds at a time, for something to
                    // become true.
                    bool Until(Func<bool> done, int limitMs)
                    {
                        for (int waited = 0; waited < limitMs && !done(); waited += 10) Pump(dispatcher, 10);
                        return done();
                    }

                    Assert.False(flyout.IsUp);
                    Assert.False(flyout.IsShowingDeviceNote);

                    // A device that disconnected: the offline glyph in amber,
                    // on the window's first show too, when it is loaded.
                    Assert.False(flyout.IsLoaded);
                    flyout.ShowDeviceNote("DualSense disconnected", connected: false);
                    OffScreen();
                    Assert.True(flyout.IsLoaded);
                    Assert.True(flyout.IsUp);
                    Assert.True(flyout.IsShowingDeviceNote);
                    Assert.True(flyout.IsVisible);
                    Assert.Equal("DualSense disconnected", flyout.StatusText.Text);
                    Assert.Equal("\uE7BA", flyout.StatusIcon.Text);
                    Assert.Equal(Color.FromRgb(0xFF, 0xB9, 0x00), Assert.IsType<SolidColorBrush>(flyout.StatusIcon.Foreground).Color);
                    Assert.Equal(TextTrimming.CharacterEllipsis, flyout.StatusText.TextTrimming);
                    // The line fits the monitor of the window in front.
                    Assert.Equal(FlyoutPlacement.LineWidth(FlyoutPlacement.Front()), flyout.StatusText.MaxWidth);
                    Assert.True(flyout.StatusText.MaxWidth >= 160 && flyout.StatusText.MaxWidth < SystemParameters.VirtualScreenWidth);

                    // A drop stays four seconds (#600 item 42), then slides
                    // away, and the window is hidden and free. Read on the
                    // clock: a pump of ten milliseconds takes longer.
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    Assert.True(Until(() => !flyout.IsVisible, 9000), "the note never left");
                    Assert.True(clock.ElapsedMilliseconds >= 3900, "a drop's note left after " + clock.ElapsedMilliseconds + " ms");
                    Assert.False(flyout.IsUp);
                    Assert.False(flyout.IsShowingDeviceNote);

                    // A device that connected: the check, in the green of
                    // the controllers turned on.
                    flyout.ShowDeviceNote("DualSense connected, battery 80%", connected: true);
                    OffScreen();
                    Assert.True(flyout.IsShowingDeviceNote);
                    Assert.Equal("\uE73E", flyout.StatusIcon.Text);
                    Assert.Equal("DualSense connected, battery 80%", flyout.StatusText.Text);
                    Assert.Equal(Color.FromRgb(0x37, 0xC8, 0x52), Assert.IsType<SolidColorBrush>(flyout.StatusIcon.Foreground).Color);

                    // It slides in, stays, and starts to slide away.
                    Assert.True(Until(() => slide.Y == 0, 3000), "the note never slid in");
                    Assert.True(Until(() => slide.Y > 1, 5000), "the note never started to slide away");
                    Assert.True(flyout.IsVisible);

                    // The controllers are turned on while it slides.
                    flyout.ShowVCsToggle(true);
                    OffScreen();
                    Assert.True(flyout.IsUp);
                    Assert.False(flyout.IsShowingDeviceNote);
                    // Well past the end of the note's slide: the window is
                    // still up for what was shown since.
                    Pump(dispatcher, 800);
                    Assert.True(flyout.IsVisible, "the note's slide hid the state shown while it ran");
                    Assert.True(flyout.IsUp);
                    Assert.Equal(Strings.Instance.Main_VCsEnabled, flyout.StatusText.Text);

                    // And that state leaves in its own time.
                    Assert.True(Until(() => !flyout.IsVisible, 5000), "the flyout never left");
                    Assert.False(flyout.IsUp);
                }
                finally
                {
                    flyout.StopTimers();
                    flyout.Close();
                }

                // A window's first show keeps the glyph's color for the
                // controllers turned off too: the theme its load applied
                // used to take it away.
                var first = new ProfileSwitchOverlay();
                try
                {
                    first.ShowVCsToggle(false);
                    first.Left = -32000;
                    first.Top = -32000;
                    Assert.True(first.IsUp);
                    Assert.False(first.IsShowingDeviceNote);
                    Assert.Equal(Color.FromRgb(0xE8, 0x1B, 0x1C), Assert.IsType<SolidColorBrush>(first.StatusIcon.Foreground).Color);
                }
                finally
                {
                    first.StopTimers();
                    first.Close();
                }
            }, TimeSpan.FromMinutes(2), "The flyout test timed out.");
        }

        /// <summary>A note changed in place (#600 item 42) keeps its place on
        /// screen: it does not slide in again, its glyph and words turn to
        /// the return, and it takes the return's two seconds from then. A
        /// count that grows keeps the time left. A note changed while it
        /// slides away stands again, and the end of that slide hides
        /// nothing.</summary>
        [Fact]
        public void TheNote_ChangesInPlace_WithNoSlide()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var flyout = new ProfileSwitchOverlay();
                try
                {
                    void OffScreen()
                    {
                        flyout.Left = -32000;
                        flyout.Top = -32000;
                    }
                    var slide = (TranslateTransform)flyout.FlyoutPanel.RenderTransform;
                    bool Until(Func<bool> done, int limitMs)
                    {
                        for (int waited = 0; waited < limitMs && !done(); waited += 10) Pump(dispatcher, 10);
                        return done();
                    }

                    flyout.ShowDeviceNote("DualSense disconnected", connected: false);
                    OffScreen();
                    Assert.True(Until(() => slide.Y == 0, 3000), "the note never slid in");

                    // The pad is back: the same note, in place.
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    flyout.UpdateDeviceNote("DualSense is back", connected: true, restartTime: true);
                    OffScreen();
                    Assert.Equal(0, slide.Y);
                    Pump(dispatcher, 100);
                    Assert.Equal(0, slide.Y);
                    Assert.True(flyout.IsShowingDeviceNote);
                    Assert.Equal("DualSense is back", flyout.StatusText.Text);
                    Assert.Equal("\uE73E", flyout.StatusIcon.Text);
                    Assert.Equal(Color.FromRgb(0x37, 0xC8, 0x52), Assert.IsType<SolidColorBrush>(flyout.StatusIcon.Foreground).Color);

                    // It leaves two seconds after the change.
                    Assert.True(Until(() => slide.Y > 1, 4000), "the note never started to slide away");
                    Assert.True(clock.ElapsedMilliseconds >= 1900, "the return's note left after " + clock.ElapsedMilliseconds + " ms");

                    // Changed while it slides away: it stands again where it
                    // shows, and stays past the end of that slide.
                    flyout.UpdateDeviceNote("DualSense disconnected", connected: false, restartTime: false);
                    OffScreen();
                    Assert.Equal(0, slide.Y);
                    Pump(dispatcher, 600);
                    Assert.True(flyout.IsVisible, "the old slide hid the note changed while it ran");
                    Assert.True(flyout.IsShowingDeviceNote);
                    Assert.Equal(0, slide.Y);
                    Assert.Equal("\uE7BA", flyout.StatusIcon.Text);

                    // A count that grows keeps the time the note has left.
                    flyout.UpdateDeviceNote("DualSense dropped 3 times in a minute", connected: false, restartTime: true);
                    OffScreen();
                    var count = System.Diagnostics.Stopwatch.StartNew();
                    Pump(dispatcher, 2000);
                    flyout.UpdateDeviceNote("DualSense dropped 4 times in a minute", connected: false, restartTime: false);
                    OffScreen();
                    Assert.True(Until(() => slide.Y > 1, 5000), "the count never left");
                    Assert.True(count.ElapsedMilliseconds < 3500 + 1500, "the count's growth gave it more time: " + count.ElapsedMilliseconds + " ms");
                    Assert.Equal("DualSense dropped 4 times in a minute", flyout.StatusText.Text);
                    Assert.True(Until(() => !flyout.IsVisible, 3000), "the note never left");

                    // With no device note up, a change in place is a new note.
                    flyout.UpdateDeviceNote("DualSense is back", connected: true, restartTime: true);
                    OffScreen();
                    Assert.True(flyout.IsShowingDeviceNote);
                    Assert.Equal(80, slide.Y);
                }
                finally
                {
                    flyout.StopTimers();
                    flyout.Close();
                }
            }, TimeSpan.FromMinutes(2), "The flyout test timed out.");
        }
    }
}
