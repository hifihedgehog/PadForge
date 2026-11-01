using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Common.Input.Twitch
{
    /// <summary>
    /// Taking turns (issue #555), Yuriy's rules from ForgePlay
    /// (src/main/command/ClaimControl.js and SkipControl.js):
    ///
    /// <list type="bullet">
    /// <item>"!claim" asks for sole control. Two other viewers approve with
    /// "!accept @name" or "!claim @name" within 60 seconds, and control lasts
    /// five minutes. Up to five requests wait. A moderator's "!claim @name"
    /// grants control at once, and "!claim status" shows the state.</item>
    /// <item>"!release" ends your own claim, and a moderator's
    /// "!release @name" ends someone else's.</item>
    /// <item>While someone holds control, only their commands and
    /// moderators' play.</item>
    /// <item>"!skip" stops the running command when two viewers ask within
    /// 30 seconds or a moderator asks once, and "!skipAll" also clears the
    /// line.</item>
    /// </list>
    ///
    /// <para>ForgePlay says each result in chat. PadForge posts nothing there,
    /// so each one is a notice the controller picture page shows. The text is
    /// English, like the rest of the web controller's pages.</para>
    /// </summary>
    internal sealed class ChatTurns
    {
        public const int ControlMs = 5 * 60 * 1000;
        public const int VoteMs = 60 * 1000;
        public const int RequiredVotes = 2;
        public const int MaxPendingRequests = 5;
        public const int SkipWindowMs = 30 * 1000;
        public const int SkipConfirmations = 2;

        private sealed class Request
        {
            public string User;
            public long ExpiresAt;
            public readonly HashSet<string> Voters = new(StringComparer.Ordinal);
        }

        private sealed class SkipVote
        {
            public long StartedAt;
            public readonly HashSet<string> Users = new(StringComparer.Ordinal);
        }

        private readonly object _lock = new();
        private readonly Func<long> _now;
        private readonly Dictionary<string, Request> _requests = new(StringComparer.Ordinal);
        private readonly SkipVote[] _skips = new SkipVote[2];
        private string _owner;
        private long _ownerExpiresAt;
        private string _notice = string.Empty;
        private int _version;

        /// <param name="now">Milliseconds on a monotonic clock.</param>
        public ChatTurns(Func<long> now = null)
        {
            _now = now ?? (() => Environment.TickCount64);
        }

        /// <summary>The latest result, for the controller picture page.</summary>
        public string Notice { get { lock (_lock) return _notice; } }

        /// <summary>Bumps when anything the picture page shows changes.</summary>
        public int Version { get { lock (_lock) return _version; } }

        /// <summary>Who holds control, or null.</summary>
        public string Owner
        {
            get { lock (_lock) { ExpireOwner(); return _owner; } }
        }

        /// <summary>The time control has left, 0 when nobody holds it.</summary>
        public long RemainingControlMs
        {
            get { lock (_lock) { ExpireOwner(); return _owner == null ? 0 : Math.Max(0, _ownerExpiresAt - _now()); } }
        }

        /// <summary>The waiting requests: who, their votes, and the time left.</summary>
        public IReadOnlyList<(string User, int Votes, long RemainingMs)> PendingRequests
        {
            get
            {
                lock (_lock)
                {
                    ExpireRequests();
                    long now = _now();
                    return _requests.Values.Select(r => (r.User, r.Voters.Count, Math.Max(0, r.ExpiresAt - now))).ToList();
                }
            }
        }

        /// <summary>True when a viewer's command may play: nobody holds
        /// control, the viewer holds it, or the viewer is a moderator.</summary>
        public bool CanPlay(string user, bool moderator)
        {
            lock (_lock)
            {
                ExpireOwner();
                return _owner == null || moderator || Same(user, _owner);
            }
        }

        /// <summary>Handles a turn-taking command. False when
        /// <paramref name="command"/> is not one, so the caller reads it as a
        /// game command. <paramref name="argument"/> is the first word after
        /// the command (ForgePlay's getCommandArgs()[0]).
        /// <paramref name="running"/> says a command is playing, which a skip
        /// needs, and <paramref name="skip"/> stops it, clearing the line too
        /// when its argument is true, and says whether anything
        /// stopped.</summary>
        public bool Handle(string user, bool moderator, string command, string argument, bool running, Func<bool, bool> skip)
        {
            switch (command)
            {
                case "claim": Claim(user, moderator, argument); return true;
                case "accept": Accept(user, argument); return true;
                case "release": Release(user, moderator, argument); return true;
                case "skip": Skip(user, moderator, running, all: false, skip); return true;
                case "skipall": Skip(user, moderator, running, all: true, skip); return true;
                default: return false;
            }
        }

        /// <summary>Ends a claim from PadForge itself, the streamer's End
        /// Claim.</summary>
        public void EndClaim()
        {
            lock (_lock)
            {
                if (_owner == null) return;
                Say($"The streamer ended @{_owner}'s control.");
                _owner = null;
            }
        }

        /// <summary>Forgets every claim, request and skip vote.</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _owner = null;
                _requests.Clear();
                _skips[0] = _skips[1] = null;
                _notice = string.Empty;
                _version++;
            }
        }

        private void Claim(string user, bool moderator, string argument)
        {
            lock (_lock)
            {
                ExpireRequests();
                if (string.IsNullOrEmpty(argument))
                {
                    RequestControl(user);
                    return;
                }
                if (string.Equals(argument, "status", StringComparison.OrdinalIgnoreCase))
                {
                    Status();
                    return;
                }
                string target = Normalize(argument);
                if (!argument.StartsWith('@') || target.Length == 0)
                {
                    Say($"@{Clean(user)}: invalid claim target {argument}.");
                    return;
                }
                if (moderator)
                {
                    string granted = _requests.TryGetValue(target, out var request) ? request.User : Clean(argument);
                    Grant(granted);
                    Say($"@{Clean(user)} gave @{granted} control for {ControlMs / 60000} minutes.");
                    return;
                }
                Vote(user, target);
            }
        }

        private void Accept(string user, string argument)
        {
            lock (_lock)
            {
                ExpireRequests();
                string target = Normalize(argument);
                if (target.Length == 0)
                {
                    Say($"@{Clean(user)}: use !accept @name.");
                    return;
                }
                Vote(user, target);
            }
        }

        // Caller holds _lock.
        private void RequestControl(string user)
        {
            ExpireOwner();
            string name = Clean(user);
            if (_owner != null) { Say($"@{name}: control already belongs to @{_owner}."); return; }
            string key = Normalize(user);
            if (key.Length == 0) return;
            if (_requests.ContainsKey(key)) { Say($"@{name}: you already asked for control."); return; }
            if (_requests.Count >= MaxPendingRequests) { Say($"@{name}: {MaxPendingRequests} requests are already waiting."); return; }
            _requests[key] = new Request { User = name, ExpiresAt = _now() + VoteMs };
            Say($"@{name} asked for control. {RequiredVotes} other viewers can approve with !accept @{name} within {VoteMs / 1000} seconds.");
        }

        // Caller holds _lock.
        private void Vote(string user, string target)
        {
            ExpireOwner();
            string name = Clean(user);
            if (_owner != null) { Say($"@{name}: control belongs to @{_owner}."); return; }
            if (!_requests.TryGetValue(target, out var request)) { Say($"@{name}: @{target} has not asked for control."); return; }
            string voter = Normalize(user);
            if (voter == target) { Say($"@{name}: you cannot approve your own request."); return; }
            if (!request.Voters.Add(voter)) { Say($"@{name}: you already approved @{request.User}."); return; }
            if (request.Voters.Count >= RequiredVotes)
            {
                Grant(request.User);
                Say($"@{request.User} has control for {ControlMs / 60000} minutes.");
                return;
            }
            long seconds = (Math.Max(0, request.ExpiresAt - _now()) + 999) / 1000;
            Say($"@{name} approved @{request.User}: {request.Voters.Count} of {RequiredVotes}, {seconds} seconds left.");
        }

        // Caller holds _lock.
        private void Status()
        {
            ExpireOwner();
            if (_owner != null)
            {
                Say($"@{_owner} has control, {(_ownerExpiresAt - _now() + 999) / 1000} seconds left.");
                return;
            }
            if (_requests.Count == 0) { Say("Nobody has control or has asked for it."); return; }
            long now = _now();
            Say("Waiting: " + string.Join(", ", _requests.Values.Select(r =>
                $"@{r.User} {r.Voters.Count} of {RequiredVotes}, {(Math.Max(0, r.ExpiresAt - now) + 999) / 1000} seconds")) + ".");
        }

        private void Release(string user, bool moderator, string argument)
        {
            lock (_lock)
            {
                ExpireOwner();
                string name = Clean(user);
                if (_owner == null) { Say($"@{name}: nobody has control."); return; }
                if (!string.IsNullOrEmpty(argument))
                {
                    if (!moderator) { Say($"@{name}: only moderators can end someone else's control."); return; }
                    string target = Normalize(argument);
                    if (target != Normalize(_owner)) { Say($"@{name}: @{Clean(argument)} does not have control."); return; }
                    Say($"@{name} ended @{_owner}'s control.");
                    _owner = null;
                    return;
                }
                if (!Same(user, _owner)) { Say($"@{name}: only @{_owner} or a moderator can end control."); return; }
                _owner = null;
                Say($"@{name} gave up control.");
            }
        }

        private void Skip(string user, bool moderator, bool running, bool all, Func<bool, bool> skip)
        {
            string name = Clean(user);
            lock (_lock)
            {
                if (!running) { Say($"@{name}: no command is playing."); return; }
                if (moderator)
                {
                    _skips[all ? 1 : 0] = null;
                }
                else
                {
                    long now = _now();
                    ref SkipVote vote = ref _skips[all ? 1 : 0];
                    if (vote == null || now - vote.StartedAt > SkipWindowMs)
                    {
                        vote = new SkipVote { StartedAt = now };
                        vote.Users.Add(Normalize(user));
                        Say($"@{name} asked to {(all ? "skip everything" : "skip")}. Another viewer can confirm within {SkipWindowMs / 1000} seconds.");
                        return;
                    }
                    if (!vote.Users.Add(Normalize(user))) { Say($"@{name}: you already asked."); return; }
                    if (vote.Users.Count < SkipConfirmations) return;
                    vote = null;
                }
            }
            // Outside the lock: the player takes its own.
            bool skipped = skip?.Invoke(all) ?? false;
            lock (_lock)
            {
                if (!skipped) Say($"@{name}: nothing was available to skip.");
                else Say(all ? $"@{name}: skipped the command and cleared the line." : $"@{name}: skipped the command.");
            }
        }

        // Caller holds _lock.
        private void Grant(string user)
        {
            _requests.Clear();
            _owner = Clean(user);
            _ownerExpiresAt = _now() + ControlMs;
        }

        // Caller holds _lock.
        private void ExpireOwner()
        {
            if (_owner != null && _now() >= _ownerExpiresAt)
            {
                _owner = null;
                _version++;
            }
        }

        // Caller holds _lock.
        private void ExpireRequests()
        {
            long now = _now();
            foreach (string key in _requests.Where(r => now >= r.Value.ExpiresAt).Select(r => r.Key).ToList())
            {
                _requests.Remove(key);
                _version++;
            }
        }

        // Caller holds _lock.
        private void Say(string notice)
        {
            _notice = notice;
            _version++;
        }

        private static bool Same(string a, string b) => Normalize(a) == Normalize(b);

        internal static string Clean(string user) => (user ?? string.Empty).Trim().TrimStart('@');

        internal static string Normalize(string user) => Clean(user).ToLowerInvariant();
    }
}
