using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Common.Input.Twitch
{
    /// <summary>Who approves a viewer's "!claim" (issue #555). Saved as its
    /// number, so the order is fixed.</summary>
    public enum TwitchClaimApproval
    {
        /// <summary>The streamer alone, the default. Viewer votes count for
        /// nothing, since one person with spare accounts could cast
        /// them all.</summary>
        Streamer = 0,

        /// <summary>The streamer and the channel's moderators.</summary>
        Moderators = 1,

        /// <summary>ForgePlay's rule: the streamer, a moderator, or any two
        /// other viewers.</summary>
        Viewers = 2,
    }

    /// <summary>
    /// Taking turns (issue #555), Yuriy's rules from ForgePlay
    /// (src/main/command/ClaimControl.js and SkipControl.js):
    ///
    /// <list type="bullet">
    /// <item>"!claim" asks for sole control for five minutes, and the request
    /// waits 60 seconds. Up to five requests wait. Who approves is the
    /// streamer's choice (<see cref="TwitchClaimApproval"/>): an approver's
    /// "!accept @name" or "!claim @name" grants control at once, and with
    /// <see cref="TwitchClaimApproval.Viewers"/> two other viewers' "!accept
    /// @name" or "!claim @name" do too. "!claim @name" from an approver needs
    /// no request, as a moderator's does in ForgePlay. ForgePlay counts a
    /// moderator's "!accept" as one viewer's vote. "!claim status" shows the
    /// state.</item>
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
    /// English, like the rest of the web controller's pages. A notice goes on
    /// the broadcast, so it never repeats what a viewer typed: an unknown
    /// target from someone who cannot approve is answered without its
    /// text.</para>
    ///
    /// <para>A viewer is known by their login, and shown by the name Twitch
    /// shows for them, which can be a translation of it. "@name" finds a
    /// request by either. A grant to a name typed with no request behind it
    /// matches that name as either, since only the viewer's next message tells
    /// which it was.</para>
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
            public string Key;
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
        private readonly Func<TwitchClaimApproval> _approval;
        private readonly Dictionary<string, Request> _requests = new(StringComparer.Ordinal);
        private readonly SkipVote[] _skips = new SkipVote[2];
        private string _owner;
        private string _ownerKey;
        private bool _ownerKeyIsLogin;
        private long _ownerExpiresAt;
        private string _notice = string.Empty;
        private int _version;

        /// <param name="now">Milliseconds on a monotonic clock.</param>
        /// <param name="approval">Who approves a claim, read on every
        /// command, so a change applies at once. The streamer's setting when
        /// null.</param>
        public ChatTurns(Func<long> now = null, Func<TwitchClaimApproval> approval = null)
        {
            _now = now ?? (() => Environment.TickCount64);
            _approval = approval ?? (() => TwitchChatRuntime.ClaimApproval);
        }

        /// <summary>Votes a request needs, 0 when viewers cannot
        /// approve.</summary>
        public int VotesNeeded => _approval() == TwitchClaimApproval.Viewers ? RequiredVotes : 0;

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
        public bool CanPlay(string user, bool moderator, string login = null)
        {
            lock (_lock)
            {
                ExpireOwner();
                return _owner == null || moderator || OwnerIs(user, login);
            }
        }

        /// <summary>Handles a turn-taking command. False when
        /// <paramref name="command"/> is not one, so the caller reads it as a
        /// game command. <paramref name="argument"/> is the first word after
        /// the command (ForgePlay's getCommandArgs()[0]).
        /// <paramref name="running"/> says a command is playing, which a skip
        /// needs, and <paramref name="skip"/> stops it, clearing the line too
        /// when its argument is true, and says whether anything
        /// stopped. <paramref name="broadcaster"/> says the streamer sent
        /// it. <paramref name="user"/> is the name shown and
        /// <paramref name="login"/> the sender's login, the name itself when
        /// null.</summary>
        public bool Handle(string user, bool moderator, string command, string argument, bool running, Func<bool, bool> skip,
            bool broadcaster = false, string login = null)
        {
            switch (command)
            {
                case "claim": Claim(user, login, Approves(moderator, broadcaster), argument); return true;
                case "accept": Accept(user, login, Approves(moderator, broadcaster), argument); return true;
                case "release": Release(user, login, moderator, argument); return true;
                case "skip": Skip(user, login, moderator, running, all: false, skip); return true;
                case "skipall": Skip(user, login, moderator, running, all: true, skip); return true;
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

        /// <summary>True when the sender approves claims alone under the
        /// streamer's setting.</summary>
        private bool Approves(bool moderator, bool broadcaster)
            => broadcaster || (moderator && _approval() != TwitchClaimApproval.Streamer);

        private void Claim(string user, string login, bool approver, string argument)
        {
            lock (_lock)
            {
                ExpireRequests();
                if (string.IsNullOrEmpty(argument))
                {
                    RequestControl(user, login);
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
                    Say($"@{Clean(user)}: use !claim or !claim @name.");
                    return;
                }
                if (approver)
                {
                    var request = FindRequest(target);
                    if (request != null) Grant(request.User, request.Key, keyIsLogin: true);
                    else Grant(Clean(argument), target, keyIsLogin: false);
                    Say($"@{Clean(user)} gave @{_owner} control for {ControlMs / 60000} minutes.");
                    return;
                }
                Vote(user, login, target);
            }
        }

        private void Accept(string user, string login, bool approver, string argument)
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
                if (approver) Approve(user, target);
                else Vote(user, login, target);
            }
        }

        // Caller holds _lock. An approver's "!accept": the request is granted
        // at once.
        private void Approve(string user, string target)
        {
            ExpireOwner();
            string name = Clean(user);
            if (_owner != null) { Say($"@{name}: control belongs to @{_owner}."); return; }
            var request = FindRequest(target);
            if (request == null) { Say($"@{name}: @{target} has not asked for control."); return; }
            Grant(request.User, request.Key, keyIsLogin: true);
            Say($"@{name} gave @{request.User} control for {ControlMs / 60000} minutes.");
        }

        // Caller holds _lock.
        private void RequestControl(string user, string login)
        {
            ExpireOwner();
            string name = Clean(user);
            if (_owner != null) { Say($"@{name}: control already belongs to @{_owner}."); return; }
            string key = Key(user, login);
            if (key.Length == 0) return;
            if (_requests.ContainsKey(key)) { Say($"@{name}: you already asked for control."); return; }
            if (_requests.Count >= MaxPendingRequests) { Say($"@{name}: {MaxPendingRequests} requests are already waiting."); return; }
            _requests[key] = new Request { User = name, Key = key, ExpiresAt = _now() + VoteMs };
            string approvers = _approval() switch
            {
                TwitchClaimApproval.Streamer => "The streamer",
                TwitchClaimApproval.Moderators => "The streamer or a moderator",
                _ => $"{RequiredVotes} other viewers",
            };
            Say($"@{name} asked for control. {approvers} can approve with !accept @{name} within {VoteMs / 1000} seconds.");
        }

        // Caller holds _lock. A viewer's "!accept @name" or "!claim @name",
        // which counts only while viewers can approve.
        private void Vote(string user, string login, string target)
        {
            ExpireOwner();
            string name = Clean(user);
            switch (_approval())
            {
                case TwitchClaimApproval.Streamer: Say($"@{name}: only the streamer approves claims."); return;
                case TwitchClaimApproval.Moderators: Say($"@{name}: only the streamer and moderators approve claims."); return;
            }
            if (_owner != null) { Say($"@{name}: control belongs to @{_owner}."); return; }
            var request = FindRequest(target);
            if (request == null) { Say($"@{name}: that viewer has not asked for control."); return; }
            string voter = Key(user, login);
            if (voter == request.Key) { Say($"@{name}: you cannot approve your own request."); return; }
            if (!request.Voters.Add(voter)) { Say($"@{name}: you already approved @{request.User}."); return; }
            if (request.Voters.Count >= RequiredVotes)
            {
                Grant(request.User, request.Key, keyIsLogin: true);
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
            bool votes = _approval() == TwitchClaimApproval.Viewers;
            Say("Waiting: " + string.Join(", ", _requests.Values.Select(r =>
                $"@{r.User} {(votes ? $"{r.Voters.Count} of {RequiredVotes}, " : "")}{(Math.Max(0, r.ExpiresAt - now) + 999) / 1000} seconds")) + ".");
        }

        private void Release(string user, string login, bool moderator, string argument)
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
                    if (target != Normalize(_owner) && target != _ownerKey) { Say($"@{name}: @{Clean(argument)} does not have control."); return; }
                    Say($"@{name} ended @{_owner}'s control.");
                    _owner = null;
                    return;
                }
                if (!OwnerIs(user, login)) { Say($"@{name}: only @{_owner} or a moderator can end control."); return; }
                _owner = null;
                Say($"@{name} gave up control.");
            }
        }

        private void Skip(string user, string login, bool moderator, bool running, bool all, Func<bool, bool> skip)
        {
            string name = Clean(user);
            string key = Key(user, login);
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
                        vote.Users.Add(key);
                        Say($"@{name} asked to {(all ? "skip everything" : "skip")}. Another viewer can confirm within {SkipWindowMs / 1000} seconds.");
                        return;
                    }
                    if (!vote.Users.Add(key)) { Say($"@{name}: you already asked."); return; }
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
        private void Grant(string shown, string key, bool keyIsLogin)
        {
            _requests.Clear();
            _owner = shown;
            _ownerKey = key;
            _ownerKeyIsLogin = keyIsLogin;
            _ownerExpiresAt = _now() + ControlMs;
        }

        // Caller holds _lock. The request of a viewer named by login, or else
        // by the name shown.
        private Request FindRequest(string target)
        {
            if (_requests.TryGetValue(target, out var request)) return request;
            foreach (var r in _requests.Values)
                if (Normalize(r.User) == target) return r;
            return null;
        }

        // Caller holds _lock. The holder of a request is known by login. A
        // name typed by an approver can be either.
        private bool OwnerIs(string user, string login)
            => _owner != null && (Key(user, login) == _ownerKey || (!_ownerKeyIsLogin && Normalize(user) == _ownerKey));

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

        private static string Key(string user, string login) => Normalize(string.IsNullOrEmpty(login) ? user : login);

        internal static string Clean(string user) => (user ?? string.Empty).Trim().TrimStart('@');

        internal static string Normalize(string user) => Clean(user).ToLowerInvariant();
    }
}
