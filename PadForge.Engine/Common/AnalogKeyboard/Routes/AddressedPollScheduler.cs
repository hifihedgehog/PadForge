using System;
using System.Collections.Generic;

namespace PadForge.Engine.Common.AnalogKeyboard.Routes
{
    /// <summary>The service classes of the Addressed poll scheduler
    /// (addressed_poll_scheduler.h:18-25).</summary>
    public enum AddressedPollClass : byte
    {
        Bound,
        Moving,
        Active,
        Recent,
        Background,
    }

    /// <summary>One <c>94 02</c> request's keys, filled by
    /// <see cref="AddressedPollScheduler.BuildPlan(ulong, AddressedPollPlan)"/>
    /// (addressed_poll_scheduler.h:27-32).</summary>
    public sealed class AddressedPollPlan
    {
        public readonly byte[] KeyIds = new byte[AddressedPollScheduler.MaxKeysPerPacket];
        public readonly AddressedPollClass[] Classes = new AddressedPollClass[AddressedPollScheduler.MaxKeysPerPacket];
        public int Count;

        public ReadOnlySpan<byte> Ids => KeyIds.AsSpan(0, Count);
    }

    /// <summary>
    /// HallJoy's Addressed poll scheduler (addressed_poll_scheduler.cpp,
    /// AGPL-3.0), ported rule for rule: which key IDs go into each
    /// <c>94 02</c> request. Pure policy with no I/O, driven by microsecond
    /// timestamps, so its tests replay HallJoy's own simulations.
    ///
    /// <para>After a reset every key is swept once in profile order. Then
    /// each request reserves two slots for the background sweep, one or two
    /// for keys that are moving, held or recently released, and fills the
    /// rest by weighted earliest deadline, with bound keys first. Empty
    /// capacity goes back to the background sweep
    /// (addressed_poll_scheduler.cpp:236-285).</para>
    /// </summary>
    public sealed class AddressedPollScheduler
    {
        public const int MaxKeysPerPacket = 9;
        public const int MaxPhysicalKeys = 255;

        // addressed_poll_scheduler.cpp:10-15
        public const ushort ActiveThresholdMilli = 8;
        public const ushort MeaningfulDeltaMilli = 2;
        public const ushort MeaningfulRawDelta = 12;
        public const ulong MovingWindowUs = 30000;
        public const ulong RecentWindowUs = 100000;
        public const int BackgroundSlotsPerPacket = 2;

        private struct KeyState
        {
            public byte KeyId;
            public ushort Hid;
            public ushort Raw;
            public ushort Milli;
            public ulong LastPollUs;
            public ulong LastChangeUs;
            public ulong RecentUntilUs;
            public bool Initialized;
            public bool Bound;
            public bool Selected;
        }

        private readonly KeyState[] _keys;
        private readonly int _count;
        private ulong _deadlineMisses;

        /// <summary>A scheduler over the profile's keys in profile order, at
        /// most 255 (addressed_poll_scheduler.cpp:18-23).</summary>
        public AddressedPollScheduler(IReadOnlyList<(byte KeyId, ushort Hid)> keys)
        {
            _count = Math.Min(keys.Count, MaxPhysicalKeys);
            _keys = new KeyState[_count];
            for (int i = 0; i < _count; i++)
            {
                _keys[i].KeyId = keys[i].KeyId;
                _keys[i].Hid = keys[i].Hid;
            }
        }

        public int Count => _count;

        /// <summary>Requests that found a key already past its class's
        /// maximum age. Statistics only.</summary>
        public ulong DeadlineMisses => _deadlineMisses;

        /// <summary>Clears every key's history but keeps its configuration
        /// and bound flag, and dates the last poll to now
        /// (addressed_poll_scheduler.cpp:25-37).</summary>
        public void Reset(ulong nowUs)
        {
            _deadlineMisses = 0;
            for (int i = 0; i < _count; i++)
            {
                var key = _keys[i];
                _keys[i] = new KeyState
                {
                    KeyId = key.KeyId,
                    Hid = key.Hid,
                    Bound = key.Bound,
                    LastPollUs = nowUs,
                };
            }
        }

        /// <summary>Marks every key with <paramref name="hid"/> bound or not
        /// (addressed_poll_scheduler.cpp:45-53).</summary>
        public void SetBound(ushort hid, bool bound)
        {
            if (hid == 0) return;
            for (int i = 0; i < _count; i++)
                if (_keys[i].Hid == hid) _keys[i].Bound = bound;
        }

        /// <summary>Marks the key with <paramref name="keyId"/> bound or not
        /// (addressed_poll_scheduler.cpp:39-43).</summary>
        public void SetPhysicalBound(byte keyId, bool bound)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_keys[i].KeyId != keyId) continue;
                _keys[i].Bound = bound;
                return;
            }
        }

        /// <summary>Records an accepted sample. A key counts as changed on its
        /// first sample or when its depth moved by 2 or its raw value by 12,
        /// and a changed or pressed key stays "recent" for 100 ms
        /// (addressed_poll_scheduler.cpp:55-83).</summary>
        public void OnSample(byte keyId, ushort raw, ushort milli, ulong nowUs)
        {
            for (int i = 0; i < _count; i++)
            {
                ref var key = ref _keys[i];
                if (key.KeyId != keyId) continue;
                bool changed = !key.Initialized
                    || Math.Abs(milli - key.Milli) >= MeaningfulDeltaMilli
                    || Math.Abs(raw - key.Raw) >= MeaningfulRawDelta;
                key.Raw = raw;
                key.Milli = milli;
                key.LastPollUs = nowUs;
                key.Initialized = true;
                if (changed)
                {
                    key.LastChangeUs = nowUs;
                    key.RecentUntilUs = nowUs + RecentWindowUs;
                }
                if (milli >= ActiveThresholdMilli)
                    key.RecentUntilUs = Math.Max(key.RecentUntilUs, nowUs + RecentWindowUs);
                return;
            }
        }

        /// <summary>The profile HID of a key, 0 when the ID is not in the
        /// profile (addressed_poll_scheduler.cpp:323-331).</summary>
        public ushort HidForKeyId(byte keyId)
        {
            for (int i = 0; i < _count; i++)
                if (_keys[i].KeyId == keyId) return _keys[i].Hid;
            return 0;
        }

        /// <summary>The class a key is in now, first match wins
        /// (addressed_poll_scheduler.cpp:85-95).</summary>
        public AddressedPollClass ClassOf(byte keyId, ulong nowUs)
        {
            for (int i = 0; i < _count; i++)
                if (_keys[i].KeyId == keyId) return Classify(in _keys[i], nowUs);
            return AddressedPollClass.Background;
        }

        private static AddressedPollClass Classify(in KeyState key, ulong nowUs)
        {
            if (key.Bound) return AddressedPollClass.Bound;
            // Unsigned, as in HallJoy: a change stamped after now never counts.
            if (key.Initialized && key.LastChangeUs != 0 && unchecked(nowUs - key.LastChangeUs) <= MovingWindowUs)
                return AddressedPollClass.Moving;
            if (key.Milli >= ActiveThresholdMilli) return AddressedPollClass.Active;
            if (key.Initialized && nowUs < key.RecentUntilUs) return AddressedPollClass.Recent;
            return AddressedPollClass.Background;
        }

        // addressed_poll_scheduler.cpp:97-134
        public static ulong TargetIntervalUs(AddressedPollClass cls) => cls switch
        {
            AddressedPollClass.Bound => 1500,
            AddressedPollClass.Moving => 2500,
            AddressedPollClass.Active => 5000,
            AddressedPollClass.Recent => 8000,
            _ => 45000,
        };

        public static ulong MaxAgeUs(AddressedPollClass cls) => cls switch
        {
            AddressedPollClass.Bound => 5000,
            AddressedPollClass.Moving => 8000,
            AddressedPollClass.Active => 10000,
            AddressedPollClass.Recent => 15000,
            _ => 50000,
        };

        public static double ClassWeight(AddressedPollClass cls) => cls switch
        {
            AddressedPollClass.Bound => 1.35,
            AddressedPollClass.Moving => 1.15,
            AddressedPollClass.Active => 1.00,
            AddressedPollClass.Recent => 0.80,
            _ => 0.25,
        };

        private static ulong Age(in KeyState key, ulong nowUs)
            => nowUs >= key.LastPollUs ? nowUs - key.LastPollUs : 0;

        // Strict comparisons throughout, so ties go to the lowest profile
        // index (addressed_poll_scheduler.cpp:148, 172, 198).
        private int FindBestClass(AddressedPollClass wanted, ulong nowUs)
        {
            int best = -1;
            double bestScore = -1.0;
            for (int i = 0; i < _count; i++)
            {
                ref readonly var key = ref _keys[i];
                if (key.Selected || Classify(in key, nowUs) != wanted) continue;
                if (!key.Initialized) return i;
                double score = (double)Age(in key, nowUs) / TargetIntervalUs(wanted);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            return best;
        }

        private int FindBestLive(ulong nowUs)
        {
            int best = -1;
            double bestScore = -1.0;
            for (int i = 0; i < _count; i++)
            {
                ref readonly var key = ref _keys[i];
                if (key.Selected) continue;
                var cls = Classify(in key, nowUs);
                if (cls != AddressedPollClass.Moving && cls != AddressedPollClass.Active && cls != AddressedPollClass.Recent)
                    continue;
                double score = ClassWeight(cls) * Age(in key, nowUs) / TargetIntervalUs(cls);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            return best;
        }

        private int FindBestPriority(ulong nowUs)
        {
            int best = -1;
            double bestScore = -1.0;
            for (int i = 0; i < _count; i++)
            {
                ref readonly var key = ref _keys[i];
                if (key.Selected) continue;
                var cls = Classify(in key, nowUs);
                if (cls == AddressedPollClass.Background) continue;
                ulong age = Age(in key, nowUs);
                double deadlineRatio = (double)age / MaxAgeUs(cls);
                double targetRatio = (double)age / TargetIntervalUs(cls);
                // A missed maximum age dominates the weighted target score.
                double score = (deadlineRatio >= 1.0 ? 1000.0 + deadlineRatio : 0.0) + ClassWeight(cls) * targetRatio;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            return best;
        }

        private int CountLive(ulong nowUs)
        {
            int count = 0;
            for (int i = 0; i < _count; i++)
            {
                var cls = Classify(in _keys[i], nowUs);
                if (cls == AddressedPollClass.Moving || cls == AddressedPollClass.Active || cls == AddressedPollClass.Recent)
                    count++;
            }
            return count;
        }

        private bool Add(AddressedPollPlan plan, int index, ulong nowUs)
        {
            if (index < 0 || plan.Count >= MaxKeysPerPacket) return false;
            ref var key = ref _keys[index];
            if (key.Selected) return false;
            var cls = Classify(in key, nowUs);
            if (key.Initialized && Age(in key, nowUs) >= MaxAgeUs(cls)) _deadlineMisses++;
            key.Selected = true;
            plan.KeyIds[plan.Count] = key.KeyId;
            plan.Classes[plan.Count] = cls;
            plan.Count++;
            return true;
        }

        /// <summary>A fresh plan for now. Allocates, so the session reuses a
        /// plan through <see cref="BuildPlan(ulong, AddressedPollPlan)"/>.</summary>
        public AddressedPollPlan BuildPlan(ulong nowUs)
        {
            var plan = new AddressedPollPlan();
            BuildPlan(nowUs, plan);
            return plan;
        }

        /// <summary>Fills <paramref name="plan"/> with the next request's keys
        /// (addressed_poll_scheduler.cpp:236-285).</summary>
        public void BuildPlan(ulong nowUs, AddressedPollPlan plan)
        {
            plan.Count = 0;
            for (int i = 0; i < _count; i++) _keys[i].Selected = false;

            // Every key once after a reset, in profile order, so nothing is
            // published from before the session.
            for (int i = 0; i < _count && plan.Count < MaxKeysPerPacket; i++)
                if (!_keys[i].Initialized) Add(plan, i, nowUs);
            if (plan.Count != 0) return;

            // Two background slots first, so no number of bound keys can
            // starve the matrix sweep ("about 20 Hz over the complete 82-key
            // matrix at ~850 packets/s", addressed_poll_scheduler.cpp:250-252).
            for (int i = 0; i < BackgroundSlotsPerPacket; i++)
                if (!Add(plan, FindBestClass(AddressedPollClass.Background, nowUs), nowUs)) break;

            // A guaranteed share for unbound keys that are moving, held or
            // just released: one slot, two once ten or more are live.
            int live = CountLive(nowUs);
            int reserve = live != 0 ? 1 + Math.Min(1, (live - 1) / 9) : 0;
            for (int i = 0; i < reserve && plan.Count < MaxKeysPerPacket; i++)
                if (!Add(plan, FindBestLive(nowUs), nowUs)) break;

            // The rest by weighted earliest deadline among non-background keys.
            while (plan.Count < MaxKeysPerPacket)
                if (!Add(plan, FindBestPriority(nowUs), nowUs)) break;

            // Unused capacity goes back to the background sweep.
            while (plan.Count < MaxKeysPerPacket)
                if (!Add(plan, FindBestClass(AddressedPollClass.Background, nowUs), nowUs)) break;
        }
    }
}
