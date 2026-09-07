using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Engine.RemoteLink
{
    internal sealed class OwnedLinkCallbacks<TKey, TCallback> where TCallback : Delegate
    {
        private sealed class Registration : IDisposable
        {
            private OwnedLinkCallbacks<TKey, TCallback> _owner;
            internal readonly TKey Key;
            internal readonly TCallback Callback;
            internal Registration(OwnedLinkCallbacks<TKey, TCallback> owner, TKey key, TCallback callback)
            { _owner = owner; Key = key; Callback = callback; }
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null)
                    ((ICollection<KeyValuePair<TKey, Registration>>)owner._entries)
                        .Remove(new KeyValuePair<TKey, Registration>(Key, this));
            }
        }

        private readonly ConcurrentDictionary<TKey, Registration> _entries = new();
        internal bool IsEmpty => _entries.IsEmpty;
        internal IDisposable Register(TKey key, TCallback callback)
        {
            var registration = new Registration(this, key, callback);
            _entries[key] = registration;
            return registration;
        }
        internal bool TryGetValue(TKey key, out TCallback callback)
        {
            if (_entries.TryGetValue(key, out var registration))
            {
                callback = registration.Callback;
                return true;
            }
            callback = null;
            return false;
        }
    }
}
