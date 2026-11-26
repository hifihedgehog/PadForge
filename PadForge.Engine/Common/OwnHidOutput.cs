using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// What PadForge's own virtual HID keyboards and mice hold, so the
    /// low-level hooks let that output through as they let PadForge's
    /// SendInput output through.
    ///
    /// <para>SendInput output carries <see cref="InputHookManager.OutputTag"/>
    /// in dwExtraInfo, and the hooks pass it on sight. A virtual HID keyboard
    /// reaches the hooks the way a real one does: no tag, no injected flag and
    /// no device (KBDLLHOOKSTRUCT and MSLLHOOKSTRUCT name none). A key the
    /// slot sends that is also a consumed source key would be swallowed, and
    /// recorded as a physical press the slot then reads back, held down by the
    /// key's own repeats. So a sender records each key before the report that
    /// presses it goes out, and each release before the report that lets go,
    /// and the hooks ask here first.</para>
    ///
    /// <para>A key matches by the hook's key index
    /// (<see cref="RawInputListener.KeyIndex"/>) or by its scan code and E0
    /// flag, whichever the event agrees with: with Num Lock off Windows reads
    /// a keypad key as an arrow or Home, which changes the code and keeps the
    /// scan code. A key down is PadForge's from the press a sender sent until
    /// Windows delivers the release, which covers the repeats Windows makes
    /// for a held HID key, including one that lands while the release is on
    /// its way. A key up is PadForge's once for each release a sender sent. A
    /// release Windows has not delivered in two seconds is taken as lost. A
    /// physical press of a key PadForge holds reads as PadForge's, and leaves
    /// the key down on screen, as it already is.</para>
    /// </summary>
    public static class OwnHidOutput
    {
        private sealed class OwnKey
        {
            public int Index;          // the hook's key index, -1 when unknown
            public int ScanKey;        // scan code, 0x100 for the E0 form, -1 when unknown
            public int Held;           // presses sent whose release Windows has not delivered
            public int PendingUps;     // of those, the ones whose release was sent
            public long PendingUntil;  // the Clock deadline for them
        }

        private static readonly object s_lock = new();
        private static readonly List<OwnKey> s_keys = new();
        private static readonly int[] s_buttonHeld = new int[5];
        private static readonly int[] s_buttonPendingUps = new int[5];
        private static readonly long[] s_buttonPendingUntil = new long[5];

        /// <summary>Nonzero while anything is recorded, so a hook event pays
        /// for no lock while no virtual keyboard or mouse is in use.</summary>
        private static int s_any;

        /// <summary>How long a sent release waits for its event before it is
        /// dropped, so a release Windows never delivered cannot later take a
        /// physical one for PadForge's.</summary>
        internal const long PendingWindowMs = 2_000;

        /// <summary>The clock the release window runs on. A test sets its own
        /// to step past the window without waiting it out.</summary>
        internal static Func<long> Clock = () => Environment.TickCount64;

        /// <summary>A scan code as this class keys it: 0x100 marks the E0
        /// form.</summary>
        public static int ScanKey(int scan, bool e0) => (scan & 0xFF) | (e0 ? 0x100 : 0);

        /// <summary>A sender is about to press the key.</summary>
        public static void NoteKeyDown(int index, int scanKey)
        {
            lock (s_lock)
            {
                var k = Find(index, scanKey) ?? Add(index, scanKey);
                k.Held++;
                Volatile.Write(ref s_any, 1);
            }
        }

        /// <summary>A sender is about to release the key. The key stays
        /// PadForge's until Windows delivers the release. False when no
        /// press of the key is waiting for a release, and nothing is
        /// recorded.</summary>
        public static bool NoteKeyUp(int index, int scanKey)
        {
            lock (s_lock)
            {
                var k = Find(index, scanKey);
                if (k == null || k.Held <= k.PendingUps) return false;
                k.PendingUps++;
                k.PendingUntil = Clock() + PendingWindowMs;
                return true;
            }
        }

        /// <summary>A press <see cref="NoteKeyDown"/> recorded went out in
        /// no report: the device refused it. It is taken back, so the
        /// key's next press and release are counted alone.</summary>
        public static void ForgetKeyDown(int index, int scanKey)
        {
            lock (s_lock)
            {
                var k = Find(index, scanKey);
                if (k == null || k.Held <= k.PendingUps) return;
                k.Held--;
                if (k.Held == 0) s_keys.Remove(k);
            }
        }

        /// <summary>A release <see cref="NoteKeyUp"/> recorded went out in
        /// no report, and the key stays down. It is taken back.</summary>
        public static void ForgetKeyUp(int index, int scanKey)
        {
            lock (s_lock)
            {
                var k = Find(index, scanKey);
                if (k == null || k.PendingUps == 0) return;
                k.PendingUps--;
            }
        }

        /// <summary>The keyboard hook's question: is this event PadForge's own
        /// HID output? A key up answered yes uses up one sent release and the
        /// press it ends.</summary>
        public static bool IsOwnKey(int index, int scan, bool e0, bool isDown)
        {
            if (Volatile.Read(ref s_any) == 0) return false;
            lock (s_lock)
            {
                Prune(Clock());
                int scanKey = ScanKey(scan, e0);
                foreach (var k in s_keys)
                {
                    bool same = (k.Index >= 0 && k.Index == index) || (k.ScanKey >= 0 && k.ScanKey == scanKey);
                    if (!same) continue;
                    if (isDown)
                    {
                        if (k.Held > 0) return true;
                        continue;
                    }
                    if (k.PendingUps > 0)
                    {
                        k.PendingUps--;
                        k.Held--;
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>A sender is about to press or release mouse button
        /// <paramref name="hookId"/>, in the hooks' numbering: 0 left, 1
        /// middle, 2 right, 3 back, 4 forward. A released button stays
        /// PadForge's until Windows delivers the release. False when nothing
        /// is recorded: an id past the five, or a release with no press
        /// waiting for one.</summary>
        public static bool NoteButton(int hookId, bool down)
        {
            if (hookId < 0 || hookId >= s_buttonHeld.Length) return false;
            lock (s_lock)
            {
                if (down)
                {
                    s_buttonHeld[hookId]++;
                    Volatile.Write(ref s_any, 1);
                    return true;
                }
                if (s_buttonHeld[hookId] <= s_buttonPendingUps[hookId]) return false;
                s_buttonPendingUps[hookId]++;
                s_buttonPendingUntil[hookId] = Clock() + PendingWindowMs;
                return true;
            }
        }

        /// <summary>A press or release <see cref="NoteButton"/> recorded
        /// went out in no report: the device refused it. It is taken
        /// back.</summary>
        public static void ForgetButton(int hookId, bool down)
        {
            if (hookId < 0 || hookId >= s_buttonHeld.Length) return;
            lock (s_lock)
            {
                if (down)
                {
                    if (s_buttonHeld[hookId] > s_buttonPendingUps[hookId]) s_buttonHeld[hookId]--;
                    return;
                }
                if (s_buttonPendingUps[hookId] > 0) s_buttonPendingUps[hookId]--;
            }
        }

        /// <summary>The mouse hook's question for a button event. A button up
        /// answered yes uses up one sent release and the press it
        /// ends.</summary>
        public static bool IsOwnButton(int hookId, bool isDown)
        {
            if (hookId < 0 || hookId >= s_buttonHeld.Length) return false;
            if (Volatile.Read(ref s_any) == 0) return false;
            lock (s_lock)
            {
                Prune(Clock());
                if (isDown) return s_buttonHeld[hookId] > 0;
                if (s_buttonPendingUps[hookId] == 0) return false;
                s_buttonPendingUps[hookId]--;
                s_buttonHeld[hookId]--;
                return true;
            }
        }

        /// <summary>Forgets everything. For the tests only: a sender that
        /// goes away records its releases instead, since other slots may still
        /// hold keys.</summary>
        internal static void ResetForTests()
        {
            lock (s_lock)
            {
                Clock = () => Environment.TickCount64;
                s_keys.Clear();
                Array.Clear(s_buttonHeld);
                Array.Clear(s_buttonPendingUps);
                Array.Clear(s_buttonPendingUntil);
                Volatile.Write(ref s_any, 0);
            }
        }

        /// <summary>Takes releases past their window as lost, with the presses
        /// they end, and drops keys nobody holds. Caller holds the
        /// lock.</summary>
        private static void Prune(long now)
        {
            for (int i = s_keys.Count - 1; i >= 0; i--)
            {
                var k = s_keys[i];
                if (k.PendingUps > 0 && now > k.PendingUntil)
                {
                    k.Held = Math.Max(0, k.Held - k.PendingUps);
                    k.PendingUps = 0;
                }
                if (k.Held == 0) s_keys.RemoveAt(i);
            }
            bool buttons = false;
            for (int b = 0; b < s_buttonHeld.Length; b++)
            {
                if (s_buttonPendingUps[b] > 0 && now > s_buttonPendingUntil[b])
                {
                    s_buttonHeld[b] = Math.Max(0, s_buttonHeld[b] - s_buttonPendingUps[b]);
                    s_buttonPendingUps[b] = 0;
                }
                if (s_buttonHeld[b] > 0) buttons = true;
            }
            if (s_keys.Count == 0 && !buttons) Volatile.Write(ref s_any, 0);
        }

        private static OwnKey Find(int index, int scanKey)
        {
            foreach (var k in s_keys)
                if (k.Index == index && k.ScanKey == scanKey) return k;
            return null;
        }

        private static OwnKey Add(int index, int scanKey)
        {
            var k = new OwnKey { Index = index, ScanKey = scanKey };
            s_keys.Add(k);
            return k;
        }
    }
}
