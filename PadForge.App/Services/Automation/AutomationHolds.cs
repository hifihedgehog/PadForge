using System;
using System.Collections.Generic;
using System.Globalization;
using PadForge.Common.Input;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// What programs hold and claim on devices (#597, #600 items 22 to 24),
    /// as the sessions keep it, and the steps that put it where the poll
    /// thread reads it (ControlHolds). Every method runs under the sessions'
    /// lock.
    ///
    /// <para>A hold is a value over a control until let go (<c>own</c>), or
    /// until the device's own input moves it (<c>set</c>). A claim takes a
    /// control from games while a subscription that asked for it lives. A
    /// session whose lease ran out lets go of both at once, and they come
    /// back at its next line. Timed steps (<c>after=</c>) wait on the poll
    /// thread's clock, each after the session's previous timed step.</para>
    /// </summary>
    internal sealed class AutomationHolds
    {
        private readonly Dictionary<(Guid Device, char Kind, int Index), (AutomationSession Owner, ControlHolds.Hold Hold)> _holds = new();
        private readonly Dictionary<AutomationSession, List<(Guid Device, char Kind, int Index, object Value)>> _claims = new();
        private readonly Dictionary<AutomationSession, long> _lastDue = new();

        /// <summary>The longest wait a timed step may ask, in ms.</summary>
        internal const int LongestAfterMs = 60000;

        /// <summary>The control another session holds among
        /// <paramref name="keys"/>, as b3 or a0, or null.</summary>
        internal string Busy(AutomationSession session, Guid device, IEnumerable<(char Kind, int Index)> keys)
        {
            foreach (var (kind, index) in keys)
                if (_holds.TryGetValue((device, kind, index), out var held) && held.Owner != session)
                    return kind + index.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>The clock tick a step of the session is due at: now, or
        /// <paramref name="ms"/> after the session's previous timed step
        /// (<c>after=</c>, or after now when that one is past), or
        /// <paramref name="ms"/> from now (<c>in=</c>, when
        /// <paramref name="fromNow"/>). A sequence chains its steps with
        /// after=, and a tap's release waits in= its time.</summary>
        internal long Due(AutomationSession session, int ms, bool fromNow = false)
        {
            if (fromNow && ms <= 0) return 0;
            long now = ControlHolds.Now();
            _lastDue.TryGetValue(session, out long last);
            long due = (fromNow ? now : Math.Max(now, last)) + ms * System.Diagnostics.Stopwatch.Frequency / 1000;
            _lastDue[session] = Math.Max(last, due);
            return due;
        }

        /// <summary>Holds controls of a device. The caller checked
        /// <see cref="Busy"/>.</summary>
        internal void Hold(AutomationSession session, Guid device, List<(char Kind, int Index, int Value)> wanted, bool untilMoved, long due)
        {
            foreach (var (kind, index, value) in wanted)
            {
                var hold = new ControlHolds.Hold(value, untilMoved);
                _holds[(device, kind, index)] = (session, hold);
                if (!session.Lapsed) ControlHolds.Send(session.Number, device, kind, index, hold, due);
            }
        }

        /// <summary>Lets go of controls of a device the session holds.</summary>
        internal void Release(AutomationSession session, Guid device, List<(char Kind, int Index)> keys, long due)
        {
            foreach (var (kind, index) in keys)
            {
                var key = (device, kind, index);
                if (!_holds.TryGetValue(key, out var held) || held.Owner != session) continue;
                _holds.Remove(key);
                if (!session.Lapsed) ControlHolds.Send(session.Number, device, kind, index, null, due);
            }
        }

        /// <summary>The controls of a device the session holds.</summary>
        internal List<(char Kind, int Index)> HeldOn(AutomationSession session, Guid device)
        {
            var list = new List<(char, int)>();
            foreach (var pair in _holds)
                if (pair.Key.Device == device && pair.Value.Owner == session) list.Add((pair.Key.Kind, pair.Key.Index));
            return list;
        }

        /// <summary>Takes a control from games for a subscription.</summary>
        internal void Claim(AutomationSession session, Guid device, char kind, int index, object value)
        {
            if (!_claims.TryGetValue(session, out var list)) _claims[session] = list = new();
            list.Add((device, kind, index, value));
            if (!session.Lapsed) ControlHolds.Send(session.Number, device, kind, index, value);
        }

        /// <summary>Gives a claimed control back, when no other subscription
        /// of the session claims it.</summary>
        internal void Unclaim(AutomationSession session, Guid device, char kind, int index, object value)
        {
            if (!_claims.TryGetValue(session, out var list)) return;
            int at = list.FindIndex(c => c.Device == device && c.Kind == kind && c.Index == index && ReferenceEquals(c.Value, value));
            if (at < 0) return;
            list.RemoveAt(at);
            bool still = list.Exists(c => c.Device == device && c.Kind == kind && c.Index == index);
            if (list.Count == 0) _claims.Remove(session);
            if (!session.Lapsed)
            {
                if (still)
                {
                    var other = list.Find(c => c.Device == device && c.Kind == kind && c.Index == index);
                    ControlHolds.Send(session.Number, device, kind, index, other.Value);
                }
                else ControlHolds.Send(session.Number, device, kind, index, null);
            }
        }

        /// <summary>How many controls the session holds and claims.</summary>
        internal int Count(AutomationSession session)
        {
            int n = 0;
            foreach (var held in _holds.Values) if (held.Owner == session) n++;
            if (_claims.TryGetValue(session, out var list)) n += list.Count;
            return n;
        }

        /// <summary>How many controls the session holds, claims apart.</summary>
        internal int Holds(AutomationSession session)
        {
            int n = 0;
            foreach (var held in _holds.Values) if (held.Owner == session) n++;
            return n;
        }

        /// <summary>The session lets go of every hold, and of every claim
        /// too unless <paramref name="claimsToo"/> is false. True when it
        /// held anything.</summary>
        internal bool ReleaseAll(AutomationSession session, bool claimsToo = true)
        {
            var mine = new List<(Guid, char, int)>();
            foreach (var pair in _holds) if (pair.Value.Owner == session) mine.Add(pair.Key);
            foreach (var key in mine) _holds.Remove(key);
            bool any = mine.Count > 0;
            if (claimsToo)
            {
                if (_claims.Remove(session)) any = true;
                _lastDue.Remove(session);
                ControlHolds.Drop(session.Number);
            }
            else
                foreach (var (device, kind, index) in mine)
                    if (!session.Lapsed) ControlHolds.Send(session.Number, device, kind, index, null);
            return any;
        }

        /// <summary>The lease ran out: everything the session holds and
        /// claims lets go at once, and is kept for its next line.</summary>
        internal void Lapse(AutomationSession session)
        {
            _lastDue.Remove(session);
            ControlHolds.Drop(session.Number);
        }

        /// <summary>The session spoke again after its lease ran out: what it
        /// held and claimed is back.</summary>
        internal void Resume(AutomationSession session)
        {
            foreach (var pair in _holds)
                if (pair.Value.Owner == session && !pair.Value.Hold.Moved)
                    ControlHolds.Send(session.Number, pair.Key.Device, pair.Key.Kind, pair.Key.Index, pair.Value.Hold);
            if (_claims.TryGetValue(session, out var list))
                foreach (var (device, kind, index, value) in list) ControlHolds.Send(session.Number, device, kind, index, value);
        }

        /// <summary>The sessions' clock: a set hold the device's own input
        /// moved is let go for good.</summary>
        internal void Purge()
        {
            List<(Guid, char, int)> moved = null;
            foreach (var pair in _holds)
                if (pair.Value.Hold.Moved) (moved ??= new()).Add(pair.Key);
            if (moved == null) return;
            foreach (var key in moved)
            {
                var owner = _holds[key].Owner;
                _holds.Remove(key);
                if (!owner.Lapsed) ControlHolds.Send(owner.Number, key.Item1, key.Item2, key.Item3, null);
            }
        }
    }
}
