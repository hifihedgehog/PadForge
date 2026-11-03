using System;
using System.Threading;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The shared worker beside a Lenovo program (#557): while a program
    /// the keyboard's model names runs, nothing goes to the keyboard, no
    /// hand-back either, and what PadForge left in the profile stays owed
    /// until the program has stopped.
    /// </summary>
    public partial class HidLightingBackendTests
    {
        /// <summary>Set: the worker's next wait inside a session holds
        /// until it is reset, and <see cref="_held"/> says it is there.</summary>
        private volatile ManualResetEventSlim _hold;
        private readonly ManualResetEventSlim _held = new(false);

        /// <summary>The wait a session takes between an exchange and the
        /// next: it moves the test's clock.</summary>
        private void Wait(int ms)
        {
            var hold = _hold;
            if (hold != null)
            {
                _held.Set();
                hold.Wait(30000);
            }
            Advance(ms);
        }

        private static void ProgramRuns(string image)
            => PeripheralOutputs.Presence = PeripheralPresence.WithRunning(PeripheralPresence.None, new[] { image });

        private static void NoProgramRuns()
            => PeripheralOutputs.Presence = PeripheralPresence.WithRunning(PeripheralPresence.None, new[] { "explorer.exe" });

        private static void Withdraw() => PeripheralOutputs.PublishLinks(new LinkTable(new[]
        {
            new DeviceLinks { Device = Device, LeftToProgram = "Lenovo Vantage", HasVendorProgram = true },
        }));

        [Fact]
        public void TheUnitsSnapshot_NamesItsModelsPrograms()
        {
            using var backend = Started(Spectrum);
            Assert.Same(VendorPrograms.LenovoSpectrum, Assert.Single(backend.Snapshot.Units).Programs);
        }

        [Fact]
        public void AProgramThatStarts_TakesTheKeyboard_WithNoHandBack()
        {
            using var backend = Lit();
            Assert.Equal(VendorPrograms.LenovoSpectrum, Assert.Single(backend.Recorded).Programs);
            int before = _keyboard.Calls().Count;

            // The link pass publishes the presence, then the table that
            // withdraws the path.
            ProgramRuns("LenovoVantage.exe");
            Withdraw();
            Passes(backend);
            Assert.Equal(before, _keyboard.Calls().Count);
            Assert.True(Channel().Disposed);
            Assert.Empty(backend.Recorded);
            // The picture stays in the profile, and what it held stays on
            // record for when the program has stopped.
            Assert.Equal(Picture(5, 0x112233), _keyboard.Stored(5));
            Assert.Equal(Original5(), KeyboardProfileBackups.Get(Product)?.Contents);
            Assert.False(Assert.Single(backend.Snapshot.Units).Asleep);
        }

        /// <summary>Between the presence and the table that follows it, the
        /// path is still linked and claimed, and nothing goes out: no
        /// picture, and no hand-back once the claim ends.</summary>
        [Fact]
        public void WhileTheProgramIsSeenAndThePathStillLinked_NothingGoesOut()
        {
            using var backend = Lit();
            int before = _keyboard.Calls().Count;
            ProgramRuns("LSDaemon.exe");
            Light(0x445566);
            Advance(LenovoLegionLighting.RepaintMs);
            Passes(backend);
            Advance(LenovoLegionLighting.HoldMs + LenovoLegionLighting.RecheckMs);
            Passes(backend);
            Assert.Equal(before, _keyboard.Calls().Count);

            Unlight();
            Passes(backend);
            Assert.Equal(before, _keyboard.Calls().Count);
            Assert.Equal(Picture(5, 0x112233), _keyboard.Stored(5));
            Assert.NotNull(KeyboardProfileBackups.Get(Product));
        }

        [Fact]
        public void WhatIsOwed_WaitsWhileTheProgramRuns_AndGoesBackOnceItStops()
        {
            using var backend = Lit();
            ProgramRuns("LZTray.exe");
            Withdraw();
            Unlight();
            Passes(backend);
            int before = _keyboard.Calls().Count;
            int opens = Opens;

            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            Advance(10_000);
            Passes(backend);
            Assert.Equal(before, _keyboard.Calls().Count);
            Assert.Equal(opens, Opens);
            Assert.NotNull(KeyboardProfileBackups.Get(Product));

            // The program stopped and no claim came back: the profile goes
            // back as an earlier run's would.
            NoProgramRuns();
            Link(PathOf(Spectrum));
            Passes(backend);
            Assert.Equal(Original5(), LenovoLegionLighting.ContentsOf(Answer(_keyboard.Stored(5)), 5));
            Assert.Null(KeyboardProfileBackups.Get(Product));
        }

        /// <summary>The program wrote the profile while it ran: the backup
        /// goes, and the program's lighting stays, as for any other
        /// program's write (Begin).</summary>
        [Fact]
        public void AProfileTheProgramWroteMeanwhile_IsLeftAsItIs_WhenTheClaimComesBack()
        {
            using var backend = Lit();
            ProgramRuns("LenovoVantage.exe");
            Withdraw();
            Passes(backend);
            var theirs = LenovoLegionLightingTests.OwnersProfile4Answer();
            theirs[1] = LenovoLegionLighting.SaveProfile;
            theirs[4] = 5;
            _keyboard.Store(5, theirs);

            NoProgramRuns();
            Link(PathOf(Spectrum));
            Light(0x0000FF);
            Passes(backend);
            // The backup of the old profile is dropped. The new session
            // backs up the program's profile before it writes its own.
            Advance(LenovoLegionLighting.HoldMs);
            Passes(backend);
            Assert.Equal(LenovoLegionLighting.ContentsOf(Answer(theirs), 5), KeyboardProfileBackups.Get(Product)?.Contents);
            Assert.Equal(Picture(5, 0x0000FF), _keyboard.Stored(5));
        }

        [Fact]
        public void Stopping_WhileTheProgramRuns_SendsNothing()
        {
            var backend = Lit();
            int before = _keyboard.Calls().Count;
            ProgramRuns("LegionSpace.exe");
            backend.Stop();
            backend.Dispose();
            Assert.Equal(before, _keyboard.Calls().Count);
            lock (_oneShots) Assert.Empty(_oneShots);
            Assert.NotNull(KeyboardProfileBackups.Get(Product));
        }

        /// <summary>The worker is held inside an exchange, so the crash path
        /// gives up waiting for it and turns to the record, which holds the
        /// restore. With a program of the model running it sends nothing.
        /// With none it sends the restore through a report of its own.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheCrashPath_SendsNothing_WhileTheProgramRuns(bool programRuns)
        {
            var backend = Lit();
            Assert.Single(backend.Recorded);
            // The recheck's read goes unanswered and waits to ask again.
            _hold = new ManualResetEventSlim(false);
            _keyboard.Mute = true;
            Advance(LenovoLegionLighting.RecheckMs);
            backend.Wake();
            Assert.True(_held.Wait(30000), "the worker reached its wait");
            if (programRuns) ProgramRuns("Lenovo.Modern.ImController.exe");

            backend.ReleaseClaimedNow(Environment.TickCount64 + 2000);
            lock (_oneShots)
            {
                if (programRuns) Assert.Empty(_oneShots);
                else Assert.Contains(_oneShots, shot => shot.Path == Spectrum.Path
                                                        && shot.Frame[1] == LenovoLegionLighting.SaveProfile);
            }
            _hold.Set();
            _hold = null;
            backend.Dispose();
        }

        /// <summary>A program the model does not name leaves the worker as
        /// it is: the claim's end hands the profile back.</summary>
        [Fact]
        public void AProgramTheModelDoesNotName_LeavesTheHandBackAsItIs()
        {
            using var backend = Lit();
            ProgramRuns("Lenovo Legion Toolkit.exe");
            Unlight();
            Passes(backend);
            Assert.Equal(Original5(), LenovoLegionLighting.ContentsOf(Answer(_keyboard.Stored(5)), 5));
            Assert.Null(KeyboardProfileBackups.Get(Product));
        }
    }
}
