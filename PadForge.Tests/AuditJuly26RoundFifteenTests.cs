using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-26 round fifteen.
    ///
    /// <para>Rounds thirteen and fourteen RECORDED an aliasing defect in
    /// the Step 4 combine and deferred it twice. Round fifteen read the
    /// whole chain firsthand instead of carrying the earlier report, and
    /// every link held: RawHidState is a struct whose Axes / Buttons /
    /// Povs / HardwareAxes are arrays, the combine assigned that struct
    /// directly (copying only the references), and five later writers then
    /// stored through the alias into the contributing device's PUBLISHED
    /// RawHidOutputState. Two separate contracts require that state to
    /// stay immutable after publish, and the UI reads it cross-thread.</para>
    ///
    /// <para>The visible consequences were a per-device preview that
    /// showed the OTHER device's presses on a two-device Extended slot,
    /// and a permanent per-tick republish: Step 3 compares its clean
    /// scratch against the polluted published state, always finds them
    /// different, and allocates a fresh copy every single tick for as long
    /// as a second device holds a button.</para>
    ///
    /// <para>These lock the copy helper the fix introduced, which is where
    /// a mistake would live. The combine loop itself has no test harness
    /// (CombineOutputStates is a private instance method needing a fully
    /// built InputManager with populated slot buffers), so the loop rewire
    /// is grounded by reading. Said plainly rather than buried, because
    /// this session has already shipped three tests that passed while
    /// proving nothing.</para></summary>
    public class AuditJuly26RoundFifteenTests
    {
        private static RawHidState Populated()
        {
            var s = RawHidState.Create(6, 40, 2);
            s.Axes[0] = 1234;
            s.Axes[3] = -4321;
            s.Buttons[0] = 0b1010;
            s.Povs[0] = 9000;
            s.Povs[1] = -1;
            s.HardwareAxes = new short[6];
            s.HardwareAxes[0] = 777;
            return s;
        }

        /// <summary>THE PROPERTY THE WHOLE FIX RESTS ON. After the copy the
        /// destination must share no array instance with the source, or the
        /// downstream writers still reach the device's published state.</summary>
        [Fact]
        public void CopyRawInto_SharesNoArrayWithTheSource()
        {
            var src = Populated();
            RawHidState dst = default;

            InputManager.CopyRawInto(ref dst, ref src);

            Assert.NotSame(src.Axes, dst.Axes);
            Assert.NotSame(src.Buttons, dst.Buttons);
            Assert.NotSame(src.Povs, dst.Povs);
            Assert.NotSame(src.HardwareAxes, dst.HardwareAxes);
        }

        /// <summary>Writing through the destination must leave the source
        /// untouched. This is the defect itself, expressed directly: the
        /// merge, the macro pass and the SOCD cleaner all store into the
        /// combined state, and every one of them used to land in the
        /// device's own published arrays.</summary>
        [Fact]
        public void WritingTheCopy_LeavesTheDeviceStateUntouched()
        {
            var src = Populated();
            RawHidState dst = default;
            InputManager.CopyRawInto(ref dst, ref src);

            dst.Axes[0] = 999;
            dst.Buttons[0] |= 0b0100;
            dst.Povs[0] = -1;

            Assert.Equal((short)1234, src.Axes[0]);
            Assert.Equal(0b1010u, src.Buttons[0]);
            Assert.Equal(9000, src.Povs[0]);
        }

        /// <summary>Values and lengths reproduce exactly, so no consumer
        /// downstream can tell the difference. A fix that quietly changed
        /// an array length would break every length-driven loop reading the
        /// combined state.</summary>
        [Fact]
        public void CopyRawInto_ReproducesLengthsAndValuesExactly()
        {
            var src = Populated();
            RawHidState dst = default;

            InputManager.CopyRawInto(ref dst, ref src);

            Assert.Equal(src.Axes.Length, dst.Axes.Length);
            Assert.Equal(src.Buttons.Length, dst.Buttons.Length);
            Assert.Equal(src.Povs.Length, dst.Povs.Length);
            Assert.Equal(src.HardwareAxes.Length, dst.HardwareAxes.Length);
            Assert.Equal(src.Axes, dst.Axes);
            Assert.Equal(src.Buttons, dst.Buttons);
            Assert.Equal(src.Povs, dst.Povs);
            Assert.Equal(src.HardwareAxes, dst.HardwareAxes);
        }

        /// <summary>The buffers are reused tick over tick. If the copy
        /// allocated every call it would trade an aliasing bug for a 1 kHz
        /// allocation on the poll thread, which is the trade this fix
        /// exists to avoid making.</summary>
        [Fact]
        public void RepeatedCopy_ReusesTheSameBuffers()
        {
            var src = Populated();
            RawHidState dst = default;

            InputManager.CopyRawInto(ref dst, ref src);
            var axes = dst.Axes;
            var buttons = dst.Buttons;
            var povs = dst.Povs;

            src.Axes[1] = 55;
            InputManager.CopyRawInto(ref dst, ref src);

            Assert.Same(axes, dst.Axes);
            Assert.Same(buttons, dst.Buttons);
            Assert.Same(povs, dst.Povs);
            Assert.Equal((short)55, dst.Axes[1]);
        }

        /// <summary>A layout change reallocates rather than copying into a
        /// mismatched buffer.</summary>
        [Fact]
        public void LayoutChange_Reallocates()
        {
            var small = RawHidState.Create(2, 8, 1);
            var large = RawHidState.Create(8, 128, 4);
            RawHidState dst = default;

            InputManager.CopyRawInto(ref dst, ref small);
            Assert.Equal(2, dst.Axes.Length);

            InputManager.CopyRawInto(ref dst, ref large);
            Assert.Equal(8, dst.Axes.Length);
            Assert.Equal(large.Buttons.Length, dst.Buttons.Length);
        }

        /// <summary>Null stays null. A never-populated device state must
        /// keep reading as absent, because the combine's own skip logic and
        /// every consumer's null guard depend on that, and an all-zero
        /// frame would read as a live centered stick instead.</summary>
        [Fact]
        public void NullSource_ClearsTheDestinationToNull()
        {
            var src = Populated();
            RawHidState dst = default;
            InputManager.CopyRawInto(ref dst, ref src);
            Assert.NotNull(dst.Axes);

            RawHidState empty = default;
            InputManager.CopyRawInto(ref dst, ref empty);

            Assert.Null(dst.Axes);
            Assert.Null(dst.Buttons);
            Assert.Null(dst.Povs);
            Assert.Null(dst.HardwareAxes);
        }
    }
}
