using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.OnScreenKeyboard;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The poll thread queues every key, button, wheel turn and move
    /// PadForge injects, and the injector thread sends them in order. Each
    /// SendInput waits on every program's low-level hook (a 30 ms hook held
    /// one SendInput for 31 ms, measured), so a key sent from the poll
    /// thread stalled the 1000 Hz loop for as long as the slowest hook ran.
    /// The test host sends nothing (DesktopInputOffLimits): these read the
    /// events through the send seam.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class InjectQueueTests : IDisposable
    {
        private const uint MouseMove = 0x0001, XDown = 0x0080, Unicode = 0x0004, KeyUp = 0x0002;
        private const ushort F24 = 0x87, F23 = 0x86, F22 = 0x85;
        private const int MarkA = 1111, MarkB = 2222, MarkC = 3333;
        private const char Snowman = '\u2603', Comet = '\u2604';

        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly List<InputManager.InjectedEvent> _sent = new();
        private int _callsOnThisThread;

        public InjectQueueTests()
        {
            InputManager.FlushPendingInput();
            InputManager.SentForTest = batch =>
            {
                lock (_sent)
                {
                    if (Environment.CurrentManagedThreadId == _thread) _callsOnThisThread++;
                    _sent.AddRange(batch);
                }
            };
        }

        public void Dispose()
        {
            InputManager.SentForTest = null;
            RestrictedField.SetValue(null, false);
        }

        private static readonly FieldInfo RestrictedField = typeof(InputManager)
            .GetField("_currentMacroSlotRestricted", BindingFlags.NonPublic | BindingFlags.Static);

        private static void Invoke(Type type, string name, params object[] args)
        {
            var method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            method.Invoke(null, args);
        }

        private static void MacroKey(ushort vk, bool up) => Invoke(typeof(InputManager), "SendKeyInput", vk, up);
        private static void MacroButton(MacroMouseButton b, bool down) => Invoke(typeof(InputManager), "SendMouseButtonInput", b, down);
        private static void MacroText(string text) => Invoke(typeof(InputManager), "SendTextInput", text, 0, text.Length);
        private static void KbmKey(ushort vk, bool down) => Invoke(typeof(KeyboardMouseVirtualController), "SendKeyboard", vk, down);

        private bool IsMine(InputManager.InjectedEvent e)
            => (!e.Keyboard && (e.Dx is MarkA or MarkB or MarkC || (e.Flags & XDown) != 0 && e.Data == 2))
               || (e.Keyboard && (e.Vk is F24 or F23 or F22 || (e.Flags & Unicode) != 0 && e.Scan is Snowman or Comet));

        private List<InputManager.InjectedEvent> Mine()
        {
            lock (_sent) return _sent.Where(IsMine).ToList();
        }

        /// <summary>Events go out in the order the poll thread made them. A
        /// move made before a click is sealed into the queue ahead of it, and
        /// a move made after the last key goes out last. The Keyboard + Mouse
        /// controller's keys and the On-Screen Keyboard's text ride the same
        /// queue as the macros'.</summary>
        [Fact]
        public void EventsGoOutInTheOrderQueued_WithAMoveSealedAheadOfAClick()
        {
            InputManager.AccumulateMouseMoveInput(MarkA, 0);
            MacroButton(MacroMouseButton.X2, down: true);
            InputManager.AccumulateMouseMoveInput(MarkB, 0);
            KbmKey(F24, down: true);
            MacroText(Snowman.ToString());
            new OskWindowsOutput(() => IntPtr.Zero, () => true).TypeText(Comet.ToString());
            MacroKey(F24, up: true);
            InputManager.AccumulateMouseMoveInput(MarkC, 0);

            Assert.Equal(0, _callsOnThisThread);
            Assert.Empty(Mine());

            InputManager.FlushPendingInput();
            var mine = Mine();
            Assert.Collection(mine,
                e => { Assert.False(e.Keyboard); Assert.Equal(MarkA, e.Dx); Assert.Equal(MouseMove, e.Flags & MouseMove); },
                e => { Assert.False(e.Keyboard); Assert.Equal(XDown, e.Flags); Assert.Equal(2u, e.Data); },
                e => { Assert.False(e.Keyboard); Assert.Equal(MarkB, e.Dx); },
                e => { Assert.True(e.Keyboard); Assert.Equal(F24, e.Vk); Assert.Equal(0u, e.Flags & KeyUp); },
                e => { Assert.Equal((ushort)Snowman, e.Scan); Assert.Equal(Unicode, e.Flags); },
                e => { Assert.Equal((ushort)Snowman, e.Scan); Assert.Equal(Unicode | KeyUp, e.Flags); },
                e => { Assert.Equal((ushort)Comet, e.Scan); Assert.Equal(Unicode, e.Flags); },
                e => { Assert.Equal((ushort)Comet, e.Scan); Assert.Equal(Unicode | KeyUp, e.Flags); },
                e => { Assert.True(e.Keyboard); Assert.Equal(F24, e.Vk); Assert.Equal(KeyUp, e.Flags & KeyUp); },
                e => { Assert.False(e.Keyboard); Assert.Equal(MarkC, e.Dx); });
        }

        /// <summary>The poll thread's sends make no SendInput call: each one
        /// only queues.</summary>
        [Fact]
        public void QueuingMakesNoSendInputCall()
        {
            MacroKey(F23, up: false);
            MacroKey(F23, up: true);
            MacroButton(MacroMouseButton.X2, down: true);
            MacroButton(MacroMouseButton.X2, down: false);
            MacroText("x");
            KbmKey(F23, down: true);
            KbmKey(F23, down: false);
            Invoke(typeof(KeyboardMouseVirtualController), "SendMouseButtonX", 2u, true);
            Invoke(typeof(KeyboardMouseVirtualController), "SendMouseButtonX", 2u, false);
            new OskWindowsOutput(() => IntPtr.Zero, () => true).TypeText("y");
            Assert.Equal(0, _callsOnThisThread);

            InputManager.FlushPendingInput();
            Assert.True(_callsOnThisThread > 0);
        }

        /// <summary>A restricted Remote Link slot queues nothing: the check
        /// runs where the event is made, on the poll thread.</summary>
        [Fact]
        public void ARestrictedSlotQueuesNothing()
        {
            RestrictedField.SetValue(null, true);
            MacroKey(F22, up: false);
            MacroKey(F22, up: true);
            MacroButton(MacroMouseButton.X2, down: true);
            MacroText(Snowman.ToString());
            RestrictedField.SetValue(null, false);

            InputManager.FlushPendingInput();
            Assert.Empty(Mine());
        }

        /// <summary>An engine stop sends everything queued before it: the
        /// injector's last act waits for the poll thread to end, so the ups
        /// its exit release queues (ReleaseAllLatchedMacroKeys,
        /// ReleasePendingPress) go out after the keys before them.</summary>
        [Fact]
        public void AStopSendsEverythingQueuedBeforeIt()
        {
            var poll = new Thread(() =>
            {
                MacroKey(F23, up: false);
                Thread.Sleep(100);
                // The exit release's up, queued after the injector's loop
                // has already seen the stop.
                MacroKey(F23, up: true);
            });
            poll.Start();

            var im = new InputManager();
            var loop = typeof(InputManager).GetMethod("MouseInjectorLoop", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(loop);
            // A stale generation: the loop sees the stop at once.
            loop.Invoke(im, new object[] { -1, poll });

            Assert.False(poll.IsAlive);
            var mine = Mine();
            Assert.Collection(mine,
                e => { Assert.Equal(F23, e.Vk); Assert.Equal(0u, e.Flags & KeyUp); },
                e => { Assert.Equal(F23, e.Vk); Assert.Equal(KeyUp, e.Flags & KeyUp); });
        }

        /// <summary>Each producer's batch goes out as its own SendInput call,
        /// as each key event did before the queue. A tap's down and up queued
        /// while the injector sat inside a slow hook would otherwise leave in
        /// one call, a press of no length (Individual calls, docs
        /// virtual-controllers.md). A Text Block slice stays one call.</summary>
        [Fact]
        public void EachQueuedBatch_IsItsOwnSendInputCall()
        {
            var calls = new List<InputManager.InjectedEvent[]>();
            InputManager.SentForTest = batch =>
            {
                if (batch.Any(e => e.Keyboard && (e.Vk == F22 || (e.Flags & Unicode) != 0 && e.Scan == Snowman)))
                    lock (calls) calls.Add(batch);
            };
            MacroKey(F22, up: false);
            MacroKey(F22, up: true);
            MacroText(new string(Snowman, 3));
            InputManager.FlushPendingInput();

            Assert.Collection(calls,
                c => { Assert.Single(c); Assert.Equal(F22, c[0].Vk); Assert.Equal(0u, c[0].Flags & KeyUp); },
                c => { Assert.Single(c); Assert.Equal(F22, c[0].Vk); Assert.Equal(KeyUp, c[0].Flags & KeyUp); },
                c => { Assert.Equal(6, c.Length); Assert.All(c, e => Assert.Equal((ushort)Snowman, e.Scan)); });
        }

        /// <summary>An event queued while another thread drains goes out with
        /// that drain, so a caller that cannot take the drain lock in time
        /// (Stop, while an injector that outlived its join still sends) can
        /// leave its events to it.</summary>
        [Fact]
        public void AnEventQueuedDuringADrain_GoesOutWithIt()
        {
            const ushort F21 = 0x84;
            bool? otherDrained = null;
            InputManager.SentForTest = batch =>
            {
                lock (_sent) _sent.AddRange(batch);
                if (otherDrained != null || !batch.Any(e => e.Keyboard && e.Vk == F23)) return;
                MacroKey(F21, up: false);
                var other = new Thread(() => otherDrained = InputManager.FlushPendingInput(lockWaitMs: 0));
                other.Start();
                other.Join();
            };
            MacroKey(F23, up: false);
            InputManager.FlushPendingInput();

            Assert.False(otherDrained);
            List<ushort> keys;
            lock (_sent) keys = _sent.Where(e => e.Keyboard && e.Vk is F23 or F21).Select(e => e.Vk).ToList();
            Assert.Equal(new[] { F23, F21 }, keys);
        }

        /// <summary>Stop tears the Keyboard + Mouse controllers down after the
        /// injector has ended, and each queues the ups of the keys and
        /// buttons it held. Stop sends them itself, or they would stay down
        /// in Windows until the next start.</summary>
        [Fact]
        public void StopSendsTheUpsTheControllersTeardownQueues()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "PadForge.App", "Common", "Input", "InputManager.cs"));
            int stop = src.IndexOf("public void Stop()", StringComparison.Ordinal);
            int injectorJoined = src.IndexOf("_mouseInjectorThread.Join(", stop, StringComparison.Ordinal);
            int destroy = src.IndexOf("DestroyAllVirtualControllers();", stop, StringComparison.Ordinal);
            int flush = src.IndexOf("FlushPendingInput(", destroy, StringComparison.Ordinal);
            int end = src.IndexOf("CloseAllDevices();", stop, StringComparison.Ordinal);
            Assert.True(stop > 0 && injectorJoined > stop && destroy > injectorJoined);
            Assert.True(flush > destroy && flush < end);
        }
    }
}
