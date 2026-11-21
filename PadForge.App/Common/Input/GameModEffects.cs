using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>
    /// What DSX game mods hold on PadForge's controllers (#529): trigger
    /// programs, lights and trigger thresholds, by the mod's controller
    /// index, each value with the sender that set it.
    ///
    /// <para>Mods address controllers by index, and every mod reviewed sends
    /// 0. In DSX the index counts connected controllers. Here it counts
    /// virtual controllers that have an online DualSense, DualSense Edge or
    /// DualShock 4 assigned, in the order Home numbers them
    /// (<see cref="SettingsManager.SlotOrders.GetGlobalSlotNumber"/>), so a
    /// DualSense on virtual controller 2 behind a wheel on 1 is still the
    /// mod's controller 0. Holds are stored by index and resolved on every
    /// pass, so the effects follow the pad when it moves.</para>
    ///
    /// <para>A held value lasts until its sender's process exits
    /// (<see cref="GameModSender"/>), until ResetToUserSettings for its
    /// index, until Clear Mod Effects, or until the server stops. Readers
    /// take a published copy without a lock, since the polling thread reads
    /// the thresholds every cycle.</para>
    /// </summary>
    internal static class GameModEffects
    {
        internal const int Controllers = InputManager.MaxPads;

        /// <summary>The values a mod can hold, each with its own sender.</summary>
        internal enum Part
        {
            LeftTrigger,
            RightTrigger,
            Lightbar,
            PlayerLights,
            MuteLight,
            LeftThreshold,
            RightThreshold,
        }

        private const int PartCount = 7;

        /// <summary>What one mod controller holds. Never changed once
        /// published: a change publishes a new one.</summary>
        internal sealed class Hold
        {
            /// <summary>11-byte trigger blocks, or null.</summary>
            internal byte[] LeftTrigger;
            internal byte[] RightTrigger;

            /// <summary>Red, green and blue, or null.</summary>
            internal byte[] Lightbar;

            /// <summary>The DualSense's player-light byte, no-fade bit
            /// included.</summary>
            internal byte? PlayerLights;

            /// <summary>The DualSense's mute light: 0 off, 1 on, 2 pulse.</summary>
            internal byte? MuteLight;

            /// <summary>The point on each trigger below which the virtual
            /// controller reports 0. Zero holds nothing.</summary>
            internal byte LeftThreshold;
            internal byte RightThreshold;

            internal readonly GameModSender[] Owners = new GameModSender[PartCount];

            internal bool HasTriggers => LeftTrigger != null || RightTrigger != null;
            internal bool HasLights => Lightbar != null || PlayerLights.HasValue || MuteLight.HasValue;
            internal bool HasThresholds => LeftThreshold != 0 || RightThreshold != 0;
            internal bool IsEmpty => !HasTriggers && !HasLights && !HasThresholds;

            /// <summary>The program the interface names: the sender of the
            /// first held value whose name could be read.</summary>
            internal string SenderName
            {
                get
                {
                    foreach (var owner in Owners)
                        if (owner?.Name != null) return owner.Name;
                    return null;
                }
            }

            internal Hold Clone()
            {
                var copy = new Hold
                {
                    LeftTrigger = LeftTrigger,
                    RightTrigger = RightTrigger,
                    Lightbar = Lightbar,
                    PlayerLights = PlayerLights,
                    MuteLight = MuteLight,
                    LeftThreshold = LeftThreshold,
                    RightThreshold = RightThreshold,
                };
                Array.Copy(Owners, copy.Owners, PartCount);
                return copy;
            }

            internal void Clear(Part part)
            {
                switch (part)
                {
                    case Part.LeftTrigger: LeftTrigger = null; break;
                    case Part.RightTrigger: RightTrigger = null; break;
                    case Part.Lightbar: Lightbar = null; break;
                    case Part.PlayerLights: PlayerLights = null; break;
                    case Part.MuteLight: MuteLight = null; break;
                    case Part.LeftThreshold: LeftThreshold = 0; break;
                    case Part.RightThreshold: RightThreshold = 0; break;
                }
                Owners[(int)part] = null;
            }
        }

        private static readonly object s_lock = new();

        /// <summary>The published holds, by controller index. Replaced
        /// whole under <see cref="s_lock"/>, read without it.</summary>
        private static Hold[] s_holds = new Hold[Controllers];

        /// <summary>True while any controller holds anything, so the
        /// polling thread's threshold read costs one field when no mod
        /// runs.</summary>
        private static volatile bool s_any;

        internal static bool AnyHeld => s_any;

        internal static Hold ForController(int index)
            => (uint)index < Controllers ? Volatile.Read(ref s_holds)[index] : null;

        /// <summary>What the mods hold for the virtual controller at
        /// <paramref name="padIndex"/>, or null. Resolves the controller
        /// order fresh. Callers may hold the devices lock but not the
        /// settings lock (the devices-before-settings order).</summary>
        internal static Hold ForSlot(int padIndex)
        {
            if (!s_any) return null;
            return ForController(Array.IndexOf(OrderedSlots(), padIndex));
        }

        // ─────────────────────────────────────────────
        //  Applying instructions
        // ─────────────────────────────────────────────

        /// <summary>Applies one instruction from <paramref name="sender"/>.
        /// A value equal to what is already held changes nothing, so a mod
        /// that resends its state every frame (the GTA V mods do) costs no
        /// writes, and only takes the value over as its owner. An index
        /// below 0 means every controller, as in DSZ.</summary>
        internal static void Apply(in GameModProtocol.Instruction instruction, GameModSender sender)
        {
            var p = instruction.Parameters;
            if (instruction.Type == GameModProtocol.GetDsxStatus || p.Length < 1) return;
            int index = p[0];
            ReadOnlySpan<int> rest = p.AsSpan(1);

            switch (instruction.Type)
            {
                case GameModProtocol.TriggerUpdate:
                {
                    if (rest.Length < 2) return;
                    int side = rest[0];
                    if (side != GameModProtocol.LeftTrigger && side != GameModProtocol.RightTrigger) return;
                    var block = new byte[GameModProtocol.TriggerBlockLength];
                    if (!GameModProtocol.TryEncodeTrigger(rest[1], rest.Slice(2), block)) return;
                    Set(index, side == GameModProtocol.LeftTrigger ? Part.LeftTrigger : Part.RightTrigger, sender,
                        h => Same(h.LeftTrigger, h.RightTrigger, side, block),
                        h => { if (side == GameModProtocol.LeftTrigger) h.LeftTrigger = block; else h.RightTrigger = block; });
                    return;
                }
                case GameModProtocol.RgbUpdate:
                {
                    if (!GameModProtocol.TryLightbar(rest, out byte r, out byte g, out byte b)) return;
                    var color = new[] { r, g, b };
                    Set(index, Part.Lightbar, sender,
                        h => h.Lightbar != null && h.Lightbar.AsSpan().SequenceEqual(color),
                        h => h.Lightbar = color);
                    return;
                }
                case GameModProtocol.PlayerLed:
                case GameModProtocol.PlayerLedNewRevision:
                {
                    byte bits;
                    bool ok = instruction.Type == GameModProtocol.PlayerLed
                        ? GameModProtocol.TryPlayerLights(rest, out bits)
                        : GameModProtocol.TryPlayerLightsNewRevision(rest, out bits);
                    if (!ok) return;
                    Set(index, Part.PlayerLights, sender, h => h.PlayerLights == bits, h => h.PlayerLights = bits);
                    return;
                }
                case GameModProtocol.MicLed:
                {
                    if (!GameModProtocol.TryMuteLight(rest, out byte value)) return;
                    Set(index, Part.MuteLight, sender, h => h.MuteLight == value, h => h.MuteLight = value);
                    return;
                }
                case GameModProtocol.TriggerThreshold:
                {
                    if (rest.Length < 2) return;
                    int side = rest[0];
                    byte point = (byte)Math.Clamp(rest[1], 0, 255);
                    if (side == GameModProtocol.LeftTrigger)
                        Set(index, Part.LeftThreshold, sender, h => h.LeftThreshold == point, h => h.LeftThreshold = point);
                    else if (side == GameModProtocol.RightTrigger)
                        Set(index, Part.RightThreshold, sender, h => h.RightThreshold == point, h => h.RightThreshold = point);
                    return;
                }
                case GameModProtocol.ResetToUserSettings:
                    Reset(index);
                    return;
            }
        }

        private static bool Same(byte[] left, byte[] right, int side, byte[] block)
        {
            var held = side == GameModProtocol.LeftTrigger ? left : right;
            return held != null && held.AsSpan().SequenceEqual(block);
        }

        private static void Set(int index, Part part, GameModSender sender,
            Func<Hold, bool> unchanged, Action<Hold> write)
        {
            if (index >= Controllers) return;
            List<int> changed = null;
            lock (s_lock)
            {
                var holds = (Hold[])s_holds.Clone();
                bool published = false;
                int from = index < 0 ? 0 : index;
                int to = index < 0 ? Controllers - 1 : index;
                for (int i = from; i <= to; i++)
                {
                    var current = holds[i];
                    if (current != null && unchanged(current))
                    {
                        // A threshold of 0 holds nothing and has no owner.
                        if (!IsHeld(current, part) || current.Owners[(int)part] == sender) continue;
                        // Same value from a new sender: it owns the value now,
                        // and nothing is written.
                        var owned = current.Clone();
                        owned.Owners[(int)part] = sender;
                        holds[i] = owned;
                        published = true;
                        continue;
                    }
                    var next = current?.Clone() ?? new Hold();
                    write(next);
                    next.Owners[(int)part] = IsHeld(next, part) ? sender : null;
                    // Nothing held before and nothing now (a threshold of 0
                    // on an idle controller): no change.
                    if (current == null && next.IsEmpty) continue;
                    holds[i] = next.IsEmpty ? null : next;
                    published = true;
                    (changed ??= new List<int>()).Add(i);
                }
                if (published) Publish(holds);
            }
            if (changed != null) Notify(changed);
        }

        private static bool IsHeld(Hold h, Part part) => part switch
        {
            Part.LeftTrigger => h.LeftTrigger != null,
            Part.RightTrigger => h.RightTrigger != null,
            Part.Lightbar => h.Lightbar != null,
            Part.PlayerLights => h.PlayerLights.HasValue,
            Part.MuteLight => h.MuteLight.HasValue,
            Part.LeftThreshold => h.LeftThreshold != 0,
            Part.RightThreshold => h.RightThreshold != 0,
            _ => false,
        };

        // ─────────────────────────────────────────────
        //  Releasing
        // ─────────────────────────────────────────────

        /// <summary>ResetToUserSettings: everything the controller holds
        /// ends, whoever set it, as DSX discards "any modifications sent to
        /// the controller" (the v3.1 example's comment).</summary>
        internal static void Reset(int index)
        {
            if (index >= Controllers) return;
            Release(i => index < 0 || i == index, _ => true);
        }

        /// <summary>Every value <paramref name="sender"/> set ends: its
        /// process exited, or it went quiet past the lapse.</summary>
        internal static void ReleaseSender(GameModSender sender)
        {
            if (sender == null) return;
            Release(_ => true, owner => owner == sender);
        }

        /// <summary>Clear Mod Effects on a device's Devices panel: whatever
        /// the mods hold on each virtual controller the device feeds.</summary>
        internal static void ClearDevice(Guid deviceGuid)
        {
            if (!s_any || deviceGuid == Guid.Empty) return;
            var slots = SlotsFedBy(deviceGuid);
            if (slots.Count == 0) return;
            var order = OrderedSlots();
            foreach (int slot in slots)
            {
                int index = Array.IndexOf(order, slot);
                if (index >= 0) Reset(index);
            }
        }

        /// <summary>Everything ends: the server stopped.</summary>
        internal static void ClearAll() => Release(_ => true, _ => true);

        private static void Release(Func<int, bool> controller, Func<GameModSender, bool> owner)
        {
            List<int> changed = null;
            lock (s_lock)
            {
                var holds = (Hold[])s_holds.Clone();
                for (int i = 0; i < Controllers; i++)
                {
                    var current = holds[i];
                    if (current == null || !controller(i)) continue;
                    Hold next = null;
                    for (int part = 0; part < PartCount; part++)
                    {
                        var who = current.Owners[part];
                        if (who == null || !owner(who)) continue;
                        next ??= current.Clone();
                        next.Clear((Part)part);
                    }
                    if (next == null) continue;
                    holds[i] = next.IsEmpty ? null : next;
                    (changed ??= new List<int>()).Add(i);
                }
                if (changed != null) Publish(holds);
            }
            if (changed != null) Notify(changed);
        }

        private static void Publish(Hold[] holds)
        {
            bool any = false;
            foreach (var h in holds)
                if (h != null) { any = true; break; }
            Volatile.Write(ref s_holds, holds);
            s_any = any;
            Interlocked.Increment(ref s_version);
        }

        private static long s_version;

        /// <summary>Bumped on every publish. The interface compares it on its
        /// own timer to know when to rebuild its lines.</summary>
        internal static long Version => Interlocked.Read(ref s_version);

        /// <summary>Tells each affected virtual controller's effects
        /// dispatcher and the lighting rows that follow the game's color.
        /// With no Sony pad on any virtual controller there is no one to
        /// tell.</summary>
        private static void Notify(List<int> controllers)
        {
            var order = OrderedSlots();
            foreach (int index in controllers)
            {
                if ((uint)index >= (uint)order.Length) continue;
                NotifySlot(order[index]);
            }
        }

        /// <summary>Queues a pass on one virtual controller's effects
        /// dispatcher and its lighting rows. Both only queue, so any thread
        /// may call it.</summary>
        private static void NotifySlot(int slot)
        {
            NotifiedForTest?.Invoke(slot);
            UserEffectsDispatcher.NotifyModEffectsChanged(slot);
            Peripherals.GameLightbarCapture.NotifyChanged(slot);
        }

        /// <summary>Test seam: sees every slot told.</summary>
        internal static Action<int> NotifiedForTest;

        // ─────────────────────────────────────────────
        //  Controller order
        // ─────────────────────────────────────────────

        private const ushort SonyVid = 0x054C;

        /// <summary>DualSense, DualSense Edge, DualShock 4 v1 and v2, and
        /// Sony's wireless adapter, the pads the Sony effects pass drives
        /// (UserEffectsDispatcher's PidStandard, PidEdge and Ds4Pid_*).</summary>
        internal static bool IsSonyPad(ushort vendorId, ushort productId)
            => vendorId == SonyVid
               && productId is 0x0CE6 or 0x0DF2 or 0x05C4 or 0x09CC or 0x0BA0;

        internal static bool IsDualSense(ushort vendorId, ushort productId)
            => vendorId == SonyVid && productId is 0x0CE6 or 0x0DF2;

        /// <summary>Test seam: replaces the order the live settings give.</summary>
        internal static Func<int[]> OrderedSlotsOverrideForTest;

        /// <summary>The virtual controllers mods can address, in the order
        /// Home numbers them: created and enabled, with an online
        /// Sony pad assigned. Index 0 is the first. Takes the devices lock,
        /// then the settings lock, never both at once, and then the leaf
        /// slot-order lock.</summary>
        internal static int[] OrderedSlots()
        {
            var test = OrderedSlotsOverrideForTest;
            if (test != null) return test() ?? Array.Empty<int>();

            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            if (devices == null || settings == null) return Array.Empty<int>();

            var online = new HashSet<Guid>();
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                    if (ud != null && ud.IsOnline && IsSonyPad(ud.VendorId, ud.ProdId))
                        online.Add(ud.InstanceGuid);
            }
            if (online.Count == 0) return Array.Empty<int>();

            var slots = new List<int>(2);
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null || us.MapTo < 0 || us.MapTo >= Controllers) continue;
                    if (!online.Contains(us.InstanceGuid) || slots.Contains(us.MapTo)) continue;
                    slots.Add(us.MapTo);
                }
            }
            slots.RemoveAll(s => !SettingsManager.SlotCreated[s] || !SettingsManager.SlotEnabled[s]);
            slots.Sort((a, b) => Number(a).CompareTo(Number(b)));
            return slots.ToArray();
        }

        /// <summary>Home's number for a slot, or a number after
        /// every listed one for a slot in no group's order.</summary>
        private static int Number(int padIndex)
        {
            int n = SettingsManager.SlotOrders.GetGlobalSlotNumber(padIndex);
            return n > 0 ? n : Controllers + 1 + padIndex;
        }

        private static List<int> SlotsFedBy(Guid deviceGuid)
        {
            var slots = new List<int>(1);
            var settings = SettingsManager.UserSettings;
            if (settings == null) return slots;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us != null && us.InstanceGuid == deviceGuid && us.MapTo >= 0 && !slots.Contains(us.MapTo))
                        slots.Add(us.MapTo);
            }
            return slots;
        }

        // ─────────────────────────────────────────────
        //  Trigger thresholds (Step 5)
        // ─────────────────────────────────────────────

        private static int[] s_cachedOrder = Array.Empty<int>();
        private static long s_cachedOrderMs;
        private const long OrderCacheMs = 250;

        /// <summary>The thresholds held for a virtual controller, for the
        /// hand-off to its virtual device. Called by the polling thread every
        /// cycle, so the controller order is cached for 250 ms, the
        /// ProviderSnapshot window, and nothing is resolved while no mod
        /// holds anything.</summary>
        internal static void ThresholdsForSlot(int padIndex, out byte left, out byte right)
        {
            left = right = 0;
            var hold = ForSlotCached(padIndex);
            if (hold == null) return;
            left = hold.LeftThreshold;
            right = hold.RightThreshold;
        }

        /// <summary><see cref="ForSlot"/> for the polling thread, which asks
        /// every cycle: the controller order is resolved at most every
        /// 250 ms, and a move in it is told (<see cref="RefreshOrder"/>). The
        /// polling thread holds neither the devices lock nor the settings
        /// lock where it asks.</summary>
        internal static Hold ForSlotCached(int padIndex)
        {
            if (!s_any) return null;
            long now = Environment.TickCount64;
            var order = Volatile.Read(ref s_cachedOrder);
            if (now - Volatile.Read(ref s_cachedOrderMs) >= OrderCacheMs)
                order = RefreshOrder();
            return ForController(Array.IndexOf(order, padIndex));
        }

        /// <summary>The controller order resolved now, and cached for
        /// <see cref="ForSlotCached"/>. When it differs from the order cached
        /// before while a mod holds anything, every virtual controller in
        /// either order is told: turning a virtual controller on or off, or
        /// a Sony pad arriving or leaving, moves the pad a mod's index names,
        /// and a mod that resends unchanged values publishes nothing that
        /// would tell them. The pad the hold left then gives the mod's values
        /// back and the pad it reached takes them. The exchange hands each
        /// move to one caller. The polling thread calls this through
        /// <see cref="ForSlotCached"/> and the interface on its own timer,
        /// which covers a moment when no virtual controller polls.</summary>
        internal static int[] RefreshOrder()
        {
            long now = Environment.TickCount64;
            var order = OrderedSlots();
            Volatile.Write(ref s_cachedOrderMs, now);
            var previous = Interlocked.Exchange(ref s_cachedOrder, order);
            if (s_any && !previous.AsSpan().SequenceEqual(order))
            {
                var told = new HashSet<int>(previous);
                told.UnionWith(order);
                foreach (int slot in told) NotifySlot(slot);
            }
            return order;
        }

        /// <summary>A trigger as the virtual controller reports it: 0 below
        /// the held point, the real value at or past it, as DualSenseY
        /// applies the threshold (controllerEmulation.cpp:248-249). The
        /// point is on the mods' 0-255 scale and the trigger on PadForge's
        /// 0-65535 one.</summary>
        internal static ushort ApplyThreshold(ushort trigger, byte point)
            => point == 0 || trigger >= point * 257 ? trigger : (ushort)0;

        /// <summary>The thresholds on an Extended or Nintendo slot's raw
        /// frame. The layout's first trigger axis is the left trigger and its
        /// second the right, the pair BuildRawHidState hands HIDMaestro as
        /// the profile's named triggers. A raw trigger rests at
        /// short.MinValue, so a pull below the point becomes that. The axes
        /// are copied into <paramref name="scratch"/> and the returned frame
        /// points at the copy, so the combined state the pad page reads keeps
        /// the real pull. Buttons are untouched: a digital trigger such as a
        /// Nintendo controller's ZL has no pull left to compare.</summary>
        internal static RawHidState ApplyRawThresholds(in RawHidState raw, in CustomControllerLayout layout,
            byte left, byte right, ref short[] scratch)
        {
            var axes = raw.Axes;
            if (axes == null || (left == 0 && right == 0)) return raw;
            int leftAxis = -1, rightAxis = -1;
            for (int i = 0; i < axes.Length; i++)
            {
                if (!layout.IsTriggerSlot(i)) continue;
                if (leftAxis < 0) leftAxis = i;
                else { rightAxis = i; break; }
            }
            if (leftAxis < 0) return raw;

            if (scratch == null || scratch.Length != axes.Length) scratch = new short[axes.Length];
            Array.Copy(axes, scratch, axes.Length);
            if (left != 0) scratch[leftAxis] = ApplyRawThreshold(scratch[leftAxis], left);
            if (right != 0 && rightAxis >= 0) scratch[rightAxis] = ApplyRawThreshold(scratch[rightAxis], right);
            var copy = raw;
            copy.Axes = scratch;
            return copy;
        }

        /// <summary><see cref="ApplyThreshold"/> on a raw trigger, whose
        /// short.MinValue to short.MaxValue range is the 0-65535 one shifted
        /// by 32768.</summary>
        internal static short ApplyRawThreshold(short trigger, byte point)
            => ApplyThreshold((ushort)(trigger + 32768), point) == 0 ? short.MinValue : trigger;

        /// <summary>Test seam: drops every hold and the cached order.</summary>
        internal static void ResetForTest()
        {
            lock (s_lock)
            {
                Volatile.Write(ref s_holds, new Hold[Controllers]);
                s_any = false;
            }
            Volatile.Write(ref s_cachedOrder, Array.Empty<int>());
            Volatile.Write(ref s_cachedOrderMs, 0L);
            OrderedSlotsOverrideForTest = null;
            NotifiedForTest = null;
        }
    }
}
