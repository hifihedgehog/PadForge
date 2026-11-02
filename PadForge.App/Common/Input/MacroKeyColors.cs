using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PadForge.Common.Input.Peripherals;
using PadForge.ViewModels;

namespace PadForge.Common.Input
{
    /// <summary>One light a Set Key Colors or Clear Key Colors action names
    /// (#557): a key by its HID usage, a group of lights by name
    /// (<see cref="LightGroups"/>), or every light.</summary>
    internal readonly record struct KeyColorTarget(ushort Usage, string Group)
    {
        public const string AllToken = "all";

        public static readonly KeyColorTarget All = new(0, AllToken);

        public bool IsAll => Group == AllToken;
        public bool IsKey => Usage != 0;

        /// <summary>
        /// The targets of a saved list, in its order: "kUU" for the key with
        /// HID usage 0xUU, "gNAME" for a group and "all" for every light,
        /// split by ",". The order is the order a countdown keeps. A part
        /// the reader does not know is dropped, and so is a repeat.
        /// </summary>
        public static KeyColorTarget[] Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<KeyColorTarget>();
            var list = new List<KeyColorTarget>();
            foreach (var raw in text.Split(','))
            {
                string item = raw.Trim();
                KeyColorTarget target;
                if (item == AllToken) target = All;
                else if (item.Length > 1 && item[0] == 'k'
                         && ushort.TryParse(item.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort usage)
                         && usage != 0)
                    target = new KeyColorTarget(usage, null);
                else if (item.Length > 1 && item[0] == 'g' && LightGroups.IsValid(item.Substring(1)))
                    target = new KeyColorTarget(0, item.Substring(1));
                else continue;
                if (!list.Contains(target)) list.Add(target);
            }
            return list.ToArray();
        }

        public static string Format(IEnumerable<KeyColorTarget> targets)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<KeyColorTarget>();
            foreach (var t in targets ?? Array.Empty<KeyColorTarget>())
            {
                if (!seen.Add(t)) continue;
                string token = t.IsAll ? AllToken
                    : t.IsKey ? "k" + t.Usage.ToString("X2", CultureInfo.InvariantCulture)
                    : LightGroups.IsValid(t.Group) ? "g" + t.Group : null;
                if (token == null) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(token);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// One slot's key colors played by Set Key Colors macro actions (#557):
    /// each running action with its keys, color, style, what the other
    /// lights show, and when it ends. Step 4b writes it on the poll thread.
    /// The effect dispatcher reads it on its own thread when it lights the
    /// slot's peripherals, so every access takes the lock. Modeled on the
    /// trigger effect store (<see cref="MacroTriggerEffectState"/>).
    ///
    /// <para>The running actions stack, the newest on top, over the
    /// colors the Lighting tab gives the shift layer engaged: an action
    /// whose other lights keep their colors lets what is beneath show
    /// there, and one that turns them off or to a color hides it. An
    /// action ends at its time, when the macro that owns it lets its
    /// trigger go (the until-release hold), at a Clear Key Colors that
    /// names one of its lights, or with the engine, which drops the whole
    /// store.</para>
    /// </summary>
    public sealed class MacroKeyColorState
    {
        private sealed class Entry
        {
            /// <summary>The action that started it, so firing it again
            /// starts it over in place.</summary>
            public object Action;
            /// <summary>The macro whose trigger release ends it, or null.</summary>
            public object Owner;
            public KeyColorTarget[] Targets;
            public int Rgb;
            public MacroKeyColorStyle Style;
            public MacroKeyColorRest Rest;
            public int RestRgb;
            public long StartMs;
            /// <summary>Environment.TickCount64 at which it ends,
            /// long.MaxValue for the until-release and until-cleared
            /// holds.</summary>
            public long UntilMs;
        }

        private readonly object _lock = new();
        private readonly int _slot;
        private readonly List<Entry> _entries = new();
        private volatile bool _any;
        private volatile bool _owned;
        private volatile bool _timed;

        public MacroKeyColorState(int slot) => _slot = slot;

        /// <summary>Raised after every change, with the slot. The engine
        /// points it at the dispatcher's wake-up.</summary>
        internal static Action<int> Changed;

        /// <summary>True while an action runs.</summary>
        public bool Any => _any;

        /// <summary>True while an action waits on its macro's trigger.</summary>
        public bool AnyOwned => _owned;

        /// <summary>True while an action ends at a time, which keeps the
        /// dispatcher's writer running to paint its fade, its countdown and
        /// its end.</summary>
        public bool AnyTimed => _timed;

        /// <summary>
        /// Starts an action, or starts it over when it runs already: its
        /// lights go on top. <paramref name="untilMs"/> is long.MaxValue for
        /// a hold with no time, and <paramref name="owner"/> is the macro for
        /// the until-release hold. A held action fired again by a macro that
        /// repeats while held keeps running as it is, the way a held trigger
        /// effect does.
        /// </summary>
        internal void Play(object action, KeyColorTarget[] targets, int rgb, MacroKeyColorStyle style,
            MacroKeyColorRest rest, int restRgb, long nowMs, long untilMs, object owner)
        {
            if (action == null || targets == null || targets.Length == 0) return;
            lock (_lock)
            {
                int at = _entries.FindIndex(e => ReferenceEquals(e.Action, action));
                if (at >= 0)
                {
                    var was = _entries[at];
                    if (was.UntilMs == long.MaxValue && untilMs == long.MaxValue && was.Owner == owner
                        && was.Rgb == (rgb & 0xFFFFFF) && was.Style == style && was.Rest == rest
                        && was.RestRgb == (restRgb & 0xFFFFFF) && was.Targets.SequenceEqual(targets))
                        return;
                    _entries.RemoveAt(at);
                }
                _entries.Add(new Entry
                {
                    Action = action,
                    Owner = owner,
                    Targets = (KeyColorTarget[])targets.Clone(),
                    Rgb = rgb & 0xFFFFFF,
                    Style = style,
                    Rest = rest,
                    RestRgb = restRgb & 0xFFFFFF,
                    StartMs = nowMs,
                    UntilMs = untilMs,
                });
                Recount();
            }
            Changed?.Invoke(_slot);
        }

        /// <summary>Ends every action that lights one of
        /// <paramref name="targets"/>, or every action when it names none
        /// or names every light.</summary>
        internal void Clear(IReadOnlyCollection<KeyColorTarget> targets)
        {
            bool everything = targets == null || targets.Count == 0 || targets.Any(t => t.IsAll);
            bool changed;
            lock (_lock)
            {
                int before = _entries.Count;
                _entries.RemoveAll(e => everything || e.Targets.Any(t => t.IsAll || targets.Contains(t)));
                changed = _entries.Count != before;
                if (changed) Recount();
            }
            if (changed) Changed?.Invoke(_slot);
        }

        public void Clear() => Clear(null);

        /// <summary>Ends the actions a macro holds until its trigger
        /// releases.</summary>
        public void ReleaseOwner(object owner)
        {
            if (owner == null || !_owned) return;
            bool changed;
            lock (_lock)
            {
                changed = _entries.RemoveAll(e => e.Owner == owner) > 0;
                if (changed) Recount();
            }
            if (changed) Changed?.Invoke(_slot);
        }

        /// <summary>Ends the actions whose macro is no longer one the slot
        /// runs: deleted, turned off, or swapped out by a profile.</summary>
        public void ReleaseOwnersNotIn(Func<object, bool> stillRuns)
        {
            if (!_owned || stillRuns == null) return;
            bool changed;
            lock (_lock)
            {
                changed = _entries.RemoveAll(e => e.Owner != null && !stillRuns(e.Owner)) > 0;
                if (changed) Recount();
            }
            if (changed) Changed?.Invoke(_slot);
        }

        /// <summary>
        /// The frame the running actions paint at <paramref name="nowMs"/>
        /// over <paramref name="under"/>, the shift layer's colors, or
        /// <paramref name="under"/> itself when none runs. An action past
        /// its time ends here.
        /// </summary>
        internal KeyFrame Compose(KeyFrame under, long nowMs)
        {
            if (!_any) return under;
            bool expired = false;
            KeyFrame frame = under;
            lock (_lock)
            {
                expired = _entries.RemoveAll(e => nowMs >= e.UntilMs) > 0;
                if (expired) Recount();
                foreach (var e in _entries) frame = FrameOf(e, frame, nowMs);
            }
            if (expired) Changed?.Invoke(_slot);
            return frame;
        }

        /// <summary>One action's frame at <paramref name="nowMs"/>. Its
        /// lights take its color: whole while steady, scaled by the time
        /// left while fading, and, counting down, whole on the first of its
        /// lights in the order picked and off on the rest, one fewer lit for
        /// each equal share of its time gone. Every light takes it when it
        /// names every light. Otherwise the rest show what is beneath, are
        /// off, or take the second color.</summary>
        private static KeyFrame FrameOf(Entry e, KeyFrame below, long nowMs)
        {
            double left = e.UntilMs == long.MaxValue || e.UntilMs <= e.StartMs ? 1
                : Math.Clamp((double)(e.UntilMs - nowMs) / (e.UntilMs - e.StartMs), 0, 1);
            int color = e.Style == MacroKeyColorStyle.Fade ? Scale(e.Rgb, left) : e.Rgb;
            int lit = e.Style == MacroKeyColorStyle.Countdown
                ? (int)Math.Ceiling(e.Targets.Length * left - 1e-9)
                : e.Targets.Length;
            var keys = new Dictionary<ushort, int>();
            var groups = new Dictionary<string, int>(StringComparer.Ordinal);
            bool all = false;
            int allColor = 0;
            for (int i = 0; i < e.Targets.Length; i++)
            {
                var t = e.Targets[i];
                int c = i < lit ? color : 0;
                if (t.IsAll)
                {
                    all = true;
                    allColor = c;
                }
                else if (t.IsKey) keys[t.Usage] = c;
                else groups[t.Group] = c;
            }
            if (all) return new KeyFrame(keys, groups, null, allColor);
            return e.Rest switch
            {
                MacroKeyColorRest.Off => new KeyFrame(keys, groups, null, 0),
                MacroKeyColorRest.Color => new KeyFrame(keys, groups, null, e.RestRgb),
                _ => new KeyFrame(keys, groups, null, null, below),
            };
        }

        private static int Scale(int rgb, double by)
            => ((int)Math.Round(((rgb >> 16) & 0xFF) * by) << 16)
               | ((int)Math.Round(((rgb >> 8) & 0xFF) * by) << 8)
               | (int)Math.Round((rgb & 0xFF) * by);

        // Caller holds the lock.
        private void Recount()
        {
            _any = _entries.Count > 0;
            _owned = _entries.Exists(e => e.Owner != null);
            _timed = _entries.Exists(e => e.UntilMs != long.MaxValue);
        }
    }
}
