using System;
using System.Threading;

namespace PadForge.Services
{
    internal readonly record struct PsmPatchRequestResult(int AppliedRadios, bool Deferred);

    /// <summary>
    /// Prevents this process from enabling PSM rewriting during Wii pairing.
    /// Readback observes external changes. It cannot drain kernel callbacks or
    /// prevent another process or the driver from changing the filter.
    /// </summary>
    internal sealed class PsmPatchCoordinator
    {
        private readonly object _sync = new();
        private readonly object _restoreSync = new();
        private readonly Func<PsmPatchSnapshot> _read;
        private readonly Func<int> _disable;
        private readonly Func<bool> _allowAbsent;
        private readonly Action _restorePolicy;
        private int _users;
        private bool _verified;
        private bool _restoreNeeded;
        private PsmPatchSnapshot _baseline;
        private string _failure;

        internal PsmPatchCoordinator(Func<PsmPatchSnapshot> read, Func<int> disable,
            Func<bool> allowAbsent, Action restorePolicy)
        {
            _read = read;
            _disable = disable;
            _allowAbsent = allowAbsent;
            _restorePolicy = restorePolicy;
        }

        internal bool IsSuspended
        {
            get { lock (_sync) return _users != 0; }
        }

        internal Suspension Suspend()
        {
            lock (_sync)
            {
                if (_users == 0)
                {
                    _verified = false;
                    _restoreNeeded = false;
                    _baseline = null;
                    _failure = null;
                }
                _users++;
                return new Suspension(this);
            }
        }

        internal PsmPatchRequestResult Apply(bool enable, Func<int> apply)
        {
            lock (_sync)
            {
                if (_users != 0) _restoreNeeded = true;
                if (enable && _users != 0)
                {
                    return new(0, true);
                }
                return new(apply(), false);
            }
        }

        private bool Verify(Suspension suspension, out string reason)
        {
            lock (_sync)
            {
                if (!suspension.BelongsTo(this))
                {
                    reason = "The Wii pairing scope has ended.";
                    return false;
                }
                if (_failure != null)
                {
                    reason = _failure;
                    return false;
                }

                try
                {
                    var before = _read();
                    if (!before.Complete)
                    {
                        if (!before.ControlPresent && (before.Error == 2 || before.Error == 3
                            || before.Error == PsmPatchSnapshot.NoSuchDevice) && _allowAbsent()
                            && (!_verified || _baseline == null))
                        {
                            _verified = true;
                            reason = null;
                            return true;
                        }
                        return Fail($"PSM state could not be read completely (win32={before.Error}).", out reason);
                    }

                    _restoreNeeded = true;
                    if (_verified)
                    {
                        if (_baseline == null || !SameRadios(_baseline, before))
                            return Fail("The Bluetooth filter instances changed during Wii pairing.", out reason);
                        if (before.ArmedCount != 0)
                            return Fail("PSM rewriting was enabled again during Wii pairing.", out reason);
                        reason = null;
                        return true;
                    }

                    if (before.ArmedCount != 0 && _disable() != before.Radios.Count)
                        return Fail("Not every Bluetooth filter accepted the PSM disable request.", out reason);

                    var after = _read();
                    if (!after.Complete)
                        return Fail($"PSM disable readback was incomplete (win32={after.Error}).", out reason);
                    if (!SameRadios(before, after))
                        return Fail("The Bluetooth filter instances changed during PSM disable.", out reason);
                    if (after.ArmedCount != 0)
                        return Fail("PSM rewriting remained enabled after the disable request.", out reason);

                    _baseline = after;
                    _verified = true;
                    reason = null;
                    return true;
                }
                catch (Exception ex)
                {
                    return Fail("PSM verification failed: " + ex.Message, out reason);
                }
            }
        }

        private bool Fail(string reason, out string result)
        {
            _failure = reason;
            result = reason;
            return false;
        }

        private static bool SameRadios(PsmPatchSnapshot a, PsmPatchSnapshot b)
        {
            if (a.Radios.Count != b.Radios.Count) return false;
            for (int i = 0; i < a.Radios.Count; i++)
                if (a.Radios[i].Index != b.Radios[i].Index
                    || !string.Equals(a.Radios[i].Name, b.Radios[i].Name, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        private void Release()
        {
            bool restore;
            lock (_sync)
            {
                _users--;
                restore = _users == 0 && _restoreNeeded;
            }
            // Reconcile outside the control lock. A new scan can start while this runs,
            // so its eventual enable must pass through Apply again.
            if (restore)
            {
                // A later scan can also finish before a slow reconciliation
                // returns. Serialize restores so an older callback cannot
                // apply its captured policy after the newer one completes.
                lock (_restoreSync) _restorePolicy();
            }
        }

        internal sealed class Suspension : IDisposable
        {
            private PsmPatchCoordinator _owner;

            internal Suspension(PsmPatchCoordinator owner) => _owner = owner;

            internal bool BelongsTo(PsmPatchCoordinator owner) => ReferenceEquals(Volatile.Read(ref _owner), owner);

            internal bool VerifyDisabled(out string reason)
            {
                var owner = Volatile.Read(ref _owner);
                if (owner != null) return owner.Verify(this, out reason);
                reason = "The Wii pairing scope has ended.";
                return false;
            }

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
