using System;
using System.IO;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A shift activator has three AND legs. The first two belong to the
    /// kinds: Chord reads its second descriptor, Axis reads its gate. A
    /// trackpad wedge chorded with a button needs one more, because the wedge
    /// and its contact window already spend both. Without the third leg the
    /// translator's chord partner was dropped on the activator path only, so
    /// a key press on the chord waited for the partner while a layer or mode
    /// shift on the same chord engaged without it.
    /// </summary>
    public class ActivatorThirdLegTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static bool Read(ShiftActivator act, CustomInputState state)
        {
            var m = typeof(InputManager).GetMethod("ReadActivatorInput",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            // The device the state belongs to: the activator's own here.
            return (bool)m.Invoke(null, new object[] { act, state, 0, act.DeviceGuid });
        }

        /// <summary>The Chord kind's own two legs plus the third. All three
        /// must hold.</summary>
        [Fact]
        public void AChordWithAThirdLegWaitsForAllThree()
        {
            var state = new CustomInputState();
            var act = new ShiftActivator
            {
                Kind = "Chord",
                Descriptor = "Button 0",
                ChordSecondDescriptor = "Button 1",
                Gate2Descriptor = "Button 2",
            };

            state.Buttons[0] = true;
            Assert.False(Read(act, state));
            state.Buttons[1] = true;
            Assert.False(Read(act, state));
            state.Buttons[2] = true;
            Assert.True(Read(act, state));

            // Any one of the three letting go releases it.
            state.Buttons[2] = false;
            Assert.False(Read(act, state));
            state.Buttons[2] = true;
            state.Buttons[1] = false;
            Assert.False(Read(act, state));
        }

        /// <summary>The leg belongs to no kind, so a plain Button activator
        /// reads it too. This is what lets the translator hand the same field
        /// to whichever carrier the first two legs took.</summary>
        [Fact]
        public void ThePlainButtonKindReadsTheThirdLegToo()
        {
            var state = new CustomInputState();
            var act = new ShiftActivator
            {
                Kind = "Button",
                Descriptor = "Button 0",
                Gate2Descriptor = "Button 3",
            };

            state.Buttons[0] = true;
            Assert.False(Read(act, state));
            state.Buttons[3] = true;
            Assert.True(Read(act, state));
        }

        /// <summary>Empty is the default and every activator authored before
        /// the field has it, so an empty leg must change nothing.</summary>
        [Fact]
        public void AnEmptyThirdLegChangesNothing()
        {
            var state = new CustomInputState();
            state.Buttons[0] = true;

            Assert.True(Read(new ShiftActivator
            {
                Kind = "Button",
                Descriptor = "Button 0",
            }, state));

            Assert.True(Read(new ShiftActivator
            {
                Kind = "Button",
                Descriptor = "Button 0",
                Gate2Descriptor = "",
            }, state));
        }

        /// <summary>The leg is read before the kind is even looked at, so no
        /// kind can skip it.</summary>
        [Fact]
        public void TheLegIsReadAheadOfTheKindSwitch()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "InputManager.Step3.MappingSetEval.cs"));

            int leg = src.IndexOf("act.Gate2Descriptor", StringComparison.Ordinal);
            int kind = src.IndexOf("string kind = act.Kind ?? \"Button\";", StringComparison.Ordinal);
            Assert.True(leg > 0 && kind > 0);
            Assert.True(leg < kind, "the third leg moved below the kind switch");
        }

        /// <summary>An engaged activator's legs are held down by the user, so
        /// the keys behind them must not reach the foreground app. The Axis
        /// kind's gate was leaking that way before the third leg was added,
        /// and both are suppressed now.</summary>
        [Fact]
        public void TheHeldLegsAreSuppressedWhileEngaged()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "InputManager.Step3.MappingSetEval.cs"));

            Assert.Contains("AddPostponeKey(suppressed, a.DeviceGuid, a.GateDescriptor);", src);
            Assert.Contains("AddPostponeKey(suppressed, a.DeviceGuid, a.Gate2Descriptor);", src);
        }

        /// <summary>A gesture on the third leg has to arm its own family, or
        /// the leg reads dead and the chord can never complete.</summary>
        [Fact]
        public void AGestureOnTheLegArmsItsFamily()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "PadForge.Engine", "Touchpad", "TouchpadGestureAutoArm.cs"));

            Assert.Contains("Classify(act.Gate2Descriptor, ref need)", src);
        }

        /// <summary>A memberwise clone carries every field, which is why the
        /// copy sites are required to use it rather than hand-listing.
        /// </summary>
        [Fact]
        public void TheCloneCarriesTheThirdLeg()
        {
            var act = new ShiftActivator
            {
                Kind = "Chord",
                Descriptor = "Button 0",
                ChordSecondDescriptor = "Button 1",
                Gate2Descriptor = "Button 2",
            };

            Assert.Equal("Button 2", act.Clone().Gate2Descriptor);
        }
    }
}
