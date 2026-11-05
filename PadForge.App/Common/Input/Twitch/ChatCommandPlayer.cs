using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PadForge.Common.Input.Twitch
{
    /// <summary>A command waiting to play: who sent it, the text after the
    /// "!", and the presses and releases it plays.</summary>
    internal sealed class ChatPlay
    {
        public string User { get; init; }
        public string Login { get; init; }
        public bool Moderator { get; init; }
        public string Text { get; init; }
        public IReadOnlyList<ChatEvent> Events { get; init; }
        public int EndMs { get; init; }
    }

    /// <summary>
    /// Plays chat commands on the Twitch Chat row's buttons (issue #555), one
    /// at a time on a thread of its own, ForgePlay's CommandExecutor. Every
    /// button is released when a command ends or is skipped and stays up for
    /// <see cref="ChatCommandParser.ReleaseGapMs"/> before the next command,
    /// so two viewers' presses of one button read as two. Only the newest
    /// <see cref="MaxWaiting"/> commands wait, and the oldest drops when
    /// another arrives, DougDoug's TwitchPlays template (MAX_QUEUE_LENGTH).
    /// Button 0, Any Command, is held while a command plays.
    /// </summary>
    internal sealed class ChatCommandPlayer : IDisposable
    {
        public const int MaxWaiting = 20;

        private readonly object _lock = new();
        private readonly LinkedList<ChatPlay> _waiting = new();
        private readonly bool[] _down = new bool[ChatCommandRegistry.MaxWords + 1];
        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _thread;
        private ChatPlay _current;
        private int _skips;
        private int _version;
        private volatile bool _disposed;
        private bool _wakeClosed;

        /// <summary>Test seam: how long the buttons stayed up after the last
        /// command.</summary>
        internal long LastGapMs { get; private set; }

        public ChatCommandPlayer()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "PadForge Twitch commands" };
            _thread.Start();
        }

        /// <summary>Bumps when the playing command, the line or a button
        /// changes, for the picture page.</summary>
        public int Version { get { lock (_lock) return _version; } }

        /// <summary>True while a command plays.</summary>
        public bool IsPlaying { get { lock (_lock) return _current != null; } }

        /// <summary>Who sent the playing command and what it says, or null.</summary>
        public (string User, string Text)? Current
        {
            get { lock (_lock) return _current == null ? null : (_current.User, _current.Text); }
        }

        public int WaitingCount { get { lock (_lock) return _waiting.Count; } }

        /// <summary>The first <paramref name="max"/> commands waiting, next
        /// first, for the picture page.</summary>
        public List<(string User, string Text)> NextWaiting(int max)
        {
            var list = new List<(string User, string Text)>();
            lock (_lock)
            {
                foreach (var play in _waiting)
                {
                    if (list.Count >= max) break;
                    list.Add((play.User, play.Text));
                }
            }
            return list;
        }

        /// <summary>Copies the row's buttons, Any Command at 0.</summary>
        public void CopyButtons(Span<bool> into)
        {
            lock (_lock)
            {
                int n = Math.Min(into.Length, _down.Length);
                for (int i = 0; i < n; i++) into[i] = _down[i];
            }
        }

        /// <summary>Puts a command in line. The oldest waiting command drops
        /// when the line is full. Nothing joins the line while the streamer
        /// pauses: the pause is read under the lock its clearing of the line
        /// takes, so a command cannot slip in behind that clearing.</summary>
        public void Enqueue(ChatPlay play)
        {
            if (play == null || _disposed) return;
            lock (_lock)
            {
                if (TwitchChatRuntime.Paused) return;
                _waiting.AddLast(play);
                while (_waiting.Count > MaxWaiting) _waiting.RemoveFirst();
                _version++;
            }
            Wake();
        }

        /// <summary>Stops the playing command, and with <paramref name="all"/>
        /// clears the line too. False when there was nothing to stop or
        /// clear.</summary>
        public bool Skip(bool all)
        {
            bool any;
            lock (_lock)
            {
                any = _current != null || (all && _waiting.Count > 0);
                if (_current != null) _skips++;
                if (all) _waiting.Clear();
                _version++;
            }
            Wake();
            return any;
        }

        /// <summary>Stops the playing command and drops the waiting ones that
        /// <paramref name="mayPlay"/> refuses, for a viewer taking sole
        /// control.</summary>
        public void KeepOnly(Func<ChatPlay, bool> mayPlay)
        {
            lock (_lock)
            {
                var node = _waiting.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (!mayPlay(node.Value)) _waiting.Remove(node);
                    node = next;
                }
                if (_current != null && !mayPlay(_current)) _skips++;
                _version++;
            }
            Wake();
        }

        // Sets the event unless Dispose has closed it, under the lock Dispose
        // closes it in.
        private void Wake()
        {
            lock (_lock)
            {
                if (!_wakeClosed) _wake.Set();
            }
        }

        /// <summary>Clears the line and leaves the playing command alone.</summary>
        public void ClearWaiting()
        {
            lock (_lock)
            {
                _waiting.Clear();
                _version++;
            }
        }

        private void Run()
        {
            var clock = Stopwatch.StartNew();
            while (!_disposed)
            {
                ChatPlay play;
                int skips;
                lock (_lock)
                {
                    play = _waiting.First?.Value;
                    if (play != null)
                    {
                        _waiting.RemoveFirst();
                        _current = play;
                        _down[0] = true;
                        _version++;
                    }
                    skips = _skips;
                }
                if (play == null)
                {
                    _wake.WaitOne(1000);
                    continue;
                }

                long start = clock.ElapsedMilliseconds;
                bool stopped = false;
                foreach (var ev in play.Events)
                {
                    if (!WaitUntil(clock, start + ev.AtMs, skips)) { stopped = true; break; }
                    if ((uint)ev.Button >= (uint)_down.Length) continue;
                    lock (_lock)
                    {
                        _down[ev.Button] = ev.Down;
                        _version++;
                    }
                }
                if (!stopped) WaitUntil(clock, start + play.EndMs, skips);

                lock (_lock)
                {
                    Array.Clear(_down);
                    _version++;
                }
                // The command stays the current one through the gap, so the
                // picture page does not flash an idle line between commands.
                long up = clock.ElapsedMilliseconds;
                WaitOutGap(clock, up + ChatCommandParser.ReleaseGapMs);
                LastGapMs = clock.ElapsedMilliseconds - up;
                lock (_lock)
                {
                    _current = null;
                    _version++;
                }
            }
        }

        // Sleeps to the end of the gap. Only Dispose cuts it short: a skip
        // arriving now has already had its effect on the buttons.
        private void WaitOutGap(Stopwatch clock, long deadline)
        {
            while (!_disposed)
            {
                long left = deadline - clock.ElapsedMilliseconds;
                if (left <= 0) return;
                _wake.WaitOne((int)left);
            }
        }

        // Sleeps to the deadline. False when a skip or Dispose cut it short.
        private bool WaitUntil(Stopwatch clock, long deadline, int skips)
        {
            while (true)
            {
                if (_disposed) return false;
                lock (_lock) if (_skips != skips) return false;
                long left = deadline - clock.ElapsedMilliseconds;
                if (left <= 0) return true;
                _wake.WaitOne((int)Math.Min(left, 1000));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _wake.Set();
            // The row closes on the poll thread, so the join is short, the
            // Game Telemetry row's rule. The woken thread sees _disposed and
            // ends.
            bool ended = _thread.Join(50);
            lock (_lock)
            {
                Array.Clear(_down);
                _waiting.Clear();
                _current = null;
                // A thread still waiting on the event would throw on a
                // disposed one, so the event goes only once the thread has
                // ended.
                if (ended)
                {
                    _wakeClosed = true;
                    _wake.Dispose();
                }
            }
        }
    }
}
