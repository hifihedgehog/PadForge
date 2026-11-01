using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Common.Input.Twitch
{
    /// <summary>What the controller picture page shows of the chat (issue
    /// #555), read whole from the open row.</summary>
    internal sealed record TwitchChatSnapshot(
        string Channel,
        TwitchChatState? State,
        bool Paused,
        string PlayingUser,
        string PlayingText,
        IReadOnlyList<(string User, string Text)> Next,
        int Waiting,
        string Owner,
        long OwnerRemainingMs,
        IReadOnlyList<(string User, int Votes, long RemainingMs)> Requests,
        string Notice);

    /// <summary>
    /// The Dashboard mirror the engine reads for Twitch chat (issue #555),
    /// the shape of <see cref="Telemetry.GameTelemetryRuntime"/>: static,
    /// written by the Dashboard view model on the UI thread, read by the poll
    /// thread's device sweep, by the row and by the controller picture page.
    ///
    /// <para><see cref="Version"/> bumps on a change that needs the row
    /// reopened: the switch and the channel. Pause, Skip, Clear and End
    /// Claim act on the open row at once.</para>
    /// </summary>
    internal static class TwitchChatRuntime
    {
        private static volatile bool _enabled;
        private static volatile string _channel = string.Empty;
        private static volatile bool _paused;
        private static volatile int _version;
        private static int _pauseVersion;
        private static TwitchChatDevice _live;

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                _version++;
            }
        }

        /// <summary>The channel the row reads, as Twitch names it, or empty.
        /// Set from what the streamer typed: the name or the channel's
        /// address.</summary>
        public static string Channel
        {
            get => _channel ?? string.Empty;
            set
            {
                string v = TwitchChatClient.NormalizeChannel(value);
                if (string.Equals(_channel, v, StringComparison.Ordinal)) return;
                _channel = v;
                if (_enabled) _version++;
            }
        }

        public static int Version => _version;

        /// <summary>True while the streamer pauses chat control, when the row
        /// ignores every command. Pausing stops the command playing and
        /// clears the line. It is not saved, so PadForge starts
        /// unpaused.</summary>
        public static bool Paused
        {
            get => _paused;
            set
            {
                if (_paused == value) return;
                _paused = value;
                Interlocked.Increment(ref _pauseVersion);
                if (value) Live?.StopAll();
            }
        }

        /// <summary>Bumps when <see cref="Paused"/> flips.</summary>
        internal static int PauseVersion => Volatile.Read(ref _pauseVersion);

        /// <summary>The open row, null while chat control is off.</summary>
        internal static TwitchChatDevice Live => Volatile.Read(ref _live);

        internal static void SetLive(TwitchChatDevice row) => Volatile.Write(ref _live, row);

        /// <summary>Forgets the row only while it is still the open one, so a
        /// row closing after its successor opened leaves the successor.</summary>
        internal static void ClearLive(TwitchChatDevice row) => Interlocked.CompareExchange(ref _live, null, row);

        /// <summary>Stops the command playing. False when none was.</summary>
        public static bool Skip() => Live?.SkipCurrent() ?? false;

        /// <summary>Clears the commands waiting and leaves the one
        /// playing.</summary>
        public static void ClearLine() => Live?.ClearLine();

        /// <summary>Ends a viewer's sole control.</summary>
        public static void EndClaim() => Live?.EndClaim();

        /// <summary>Test seam: the defaults, no row.</summary>
        internal static void ResetForTest()
        {
            _enabled = false;
            _channel = string.Empty;
            _paused = false;
            Volatile.Write(ref _live, null);
        }
    }
}
