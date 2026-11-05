using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PadForge.Engine;

namespace PadForge.Common.Input.Twitch
{
    /// <summary>
    /// The Twitch Chat row (issue #555, discussion #544): a synthetic
    /// <see cref="ISdlInputDevice"/> that reads a channel's chat and presses
    /// its buttons when viewers type commands. The Game Telemetry row's shape
    /// (#531): one row while the switch is on, opened and retired by the
    /// device sweep, a fixed identity so mappings survive a change of
    /// channel, and a pooled copy on every read.
    ///
    /// <para>Its buttons are <see cref="ChatCommandRegistry"/>'s words, the
    /// voice phrase row's shape (#317): button 0 is "Any Command", held while
    /// a command plays, and each word sits at the button it was given and is
    /// named "!word".</para>
    ///
    /// <para>A message counts when it starts with "!", or with a reply's
    /// "@name" and then "!" (ComfyJS app.js 1084 and 1091). The order is
    /// ForgePlay's controller.js: a turn command is handled, a
    /// viewer without control is ignored, and anything else is parsed whole
    /// and put in line, or ignored when it does not parse. While the streamer
    /// pauses, every message is ignored. A viewer who takes sole control takes
    /// the line too: the commands of others stop and leave it, moderators'
    /// stay.</para>
    ///
    /// <para>Chatterino sends a message that repeats the last one with " " and
    /// U+034F on the end (TwitchChannel.cpp, MAGIC_MESSAGE_SUFFIX), so that
    /// Twitch takes it. No word holds such a mark
    /// (<see cref="ChatCommandRegistry.Check"/>), so marks that draw nothing
    /// at the end of a message are cut before it is read.</para>
    /// </summary>
    internal sealed class TwitchChatDevice : ISdlInputDevice
    {
        private const ushort ChatVendorId = 0x5457;  // "TW"
        private const ushort ChatProductId = 0x4348; // "CH"

        internal const int AnyCommandButton = 0;

        /// <summary>The English name of button 0. The row keeps its objects in
        /// the settings file, so the name is published in English and
        /// MappingDisplayResolver.LocalizeObjectName translates it where it
        /// is shown, the voice row's Any Phrase rule.</summary>
        internal const string AnyCommandName = "Any Command";

        /// <summary>How many waiting commands the picture page lists.</summary>
        public const int ShownWaiting = 5;

        /// <summary>The row's fixed identity, so saved mappings survive a
        /// change of channel and the picture page can find the row's
        /// slot.</summary>
        internal static readonly Guid Identity = Md5Guid("pftwitch:chat");

        private readonly object _stateLock = new();
        private readonly TwitchChatClient _client;
        private readonly ChatCommandPlayer _player = new();
        private readonly ChatTurns _turns;
        private PooledInputStatePair _statePool;
        private volatile bool _attached;
        private volatile bool _disposed;

        /// <summary>The <see cref="TwitchChatRuntime.Version"/> this row was
        /// opened under. The sweep reopens the row when it moves.</summary>
        public int ConfigVersion { get; }

        /// <summary>The channel the row reads, empty when none is set.</summary>
        public string Channel { get; }

        /// <summary>Reads the live configuration, version first, the Game
        /// Telemetry row's order: the setters bump it last.</summary>
        public static TwitchChatDevice FromCurrentSettings()
        {
            int version = TwitchChatRuntime.Version;
            return new TwitchChatDevice(TwitchChatRuntime.Channel, version);
        }

        internal TwitchChatDevice(string channel, int configVersion,
            Func<IChatSocket> newSocket = null, Func<long> now = null)
        {
            Channel = TwitchChatClient.NormalizeChannel(channel);
            ConfigVersion = configVersion;
            _turns = new ChatTurns(now);
            if (Channel.Length > 0)
            {
                _client = new TwitchChatClient(Channel, newSocket);
                _client.Message += OnMessage;
            }
            // The identity never changes, so saved mappings survive a change
            // of channel.
            Name = "Twitch Chat";
            DevicePath = "twitch://chat";
            InstanceGuid = Identity;
            ProductGuid = Md5Guid("pftwitch-product");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
        }

        // Span the range of stable word buttons, the voice row's rule, so
        // removing a word leaves a gap instead of renumbering.
        private static int ButtonSpan => 1 + ChatCommandRegistry.MaxButtonInUse;

        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => ButtonSpan;
        public int RawButtonCount => ButtonSpan;
        public int NumHats => 0;
        public int[] SupportedButtonIndices
        {
            get
            {
                var list = new List<int> { AnyCommandButton };
                foreach (var w in ChatCommandRegistry.Words) list.Add(w.Button);
                return list.ToArray();
            }
        }
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => false;
        public bool HasRumbleTriggers => false;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public bool HasTouchpad => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public bool IsAttached => _attached && !_disposed;
        public ushort VendorId => ChatVendorId;
        public ushort ProductId => ChatProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.TwitchChat;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        /// <summary>The name a word's button carries.</summary>
        internal static string ButtonName(string word) => "!" + word;

        /// <summary>Any Command, then every word at its button.</summary>
        public DeviceObjectItem[] GetDeviceObjects()
        {
            var words = ChatCommandRegistry.Words;
            var items = new DeviceObjectItem[1 + words.Count];
            items[0] = new DeviceObjectItem
            {
                Name = AnyCommandName,
                ObjectType = DeviceObjectTypeFlags.PushButton,
                ObjectTypeGuid = ObjectGuid.Button,
                InputIndex = AnyCommandButton,
            };
            for (int i = 0; i < words.Count; i++)
                items[i + 1] = new DeviceObjectItem
                {
                    Name = ButtonName(words[i].Word),
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    ObjectTypeGuid = ObjectGuid.Button,
                    InputIndex = words[i].Button,
                };
            return items;
        }

        // ─── Status, for the Dashboard, the Devices page and the picture page ───

        /// <summary>Where the connection stands, null without a channel.</summary>
        internal TwitchChatState? State => _client?.State;

        /// <summary>Bumps when anything the status line shows changes.</summary>
        internal int StatusVersion => _client?.StatusVersion ?? 0;

        /// <summary>Bumps when anything the picture page shows changes.</summary>
        internal int ShowVersion => _player.Version + _turns.Version + StatusVersion + TwitchChatRuntime.PauseVersion
            + TwitchChatRuntime.ClaimApprovalVersion;

        /// <summary>Everything the picture page shows, read now.</summary>
        internal TwitchChatSnapshot Snapshot()
        {
            var playing = _player.Current;
            return new TwitchChatSnapshot(
                Channel,
                State,
                TwitchChatRuntime.Paused,
                playing?.User,
                playing?.Text,
                _player.NextWaiting(ShownWaiting),
                _player.WaitingCount,
                _turns.Owner,
                _turns.RemainingControlMs,
                _turns.PendingRequests,
                _turns.VotesNeeded,
                _turns.Notice);
        }

        // ─── Lifecycle ───

        /// <summary>Starts reading the channel. The row opens without one too,
        /// so mappings can be made before the channel is entered.</summary>
        public bool Open()
        {
            if (_disposed) return false;
            _attached = true;
            TwitchChatRuntime.SetLive(this);
            _client?.Start();
            return true;
        }

        private void OnMessage(string login, string user, bool moderator, bool broadcaster, string text)
        {
            if (_disposed) return;
            try { HandleMessage(user, moderator, text, broadcaster, login); }
            catch (Exception ex) { SdlDiagLog.WriteLine("Twitch chat: a message failed: " + ex.Message); }
        }

        /// <summary>Acts on one chat message. The client calls it for every
        /// message, and the tests call it directly.</summary>
        internal void HandleMessage(string user, bool moderator, string text, bool broadcaster = false, string login = null)
        {
            if (_disposed || string.IsNullOrEmpty(text)) return;
            string t = TrimInvisibleEnd(text).Trim();
            // A reply starts with "@name ", and ComfyJS reads a command after
            // it too (app.js 1091 to 1097).
            if (t.Length > 0 && t[0] == '@')
            {
                int afterName = t.IndexOf(' ');
                if (afterName < 0) return;
                t = t.Substring(afterName + 1).TrimStart();
            }
            if (t.Length < 2 || t[0] != '!' || t.Length > ChatCommandParser.MaxLength) return;
            if (TwitchChatRuntime.Paused) return;
            string body = t.Substring(1).Trim();
            if (body.Length == 0) return;

            // ComfyJS splits the message at its first space and lower-cases
            // the first word, the command (app.js 1086 and 1087). ForgePlay
            // reads that word as the command and the next one as its argument
            // (ChatMessage.getCommandArgs()[0]), and parses the first word
            // and the rest as one sequence.
            int space = body.IndexOf(' ');
            string command = (space < 0 ? body : body.Substring(0, space)).ToLowerInvariant();
            string rest = space < 0 ? string.Empty : body.Substring(space + 1);
            string[] args = rest.Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
            string argument = args.Length > 0 ? args[0] : string.Empty;
            if (_turns.Handle(user, moderator, command, argument, _player.IsPlaying, all => _player.Skip(all), broadcaster, login))
            {
                if (_turns.Owner != null) _player.KeepOnly(p => _turns.CanPlay(p.User, p.Moderator, p.Login));
                return;
            }
            if (!_turns.CanPlay(user, moderator, login)) return;

            string sequence = rest.Length > 0 ? command + " " + rest : command;
            var steps = ChatCommandParser.Parse(sequence, ChatCommandRegistry.ButtonFor);
            if (steps == null) return;
            var events = ChatCommandParser.Timeline(steps, out int endMs);
            _player.Enqueue(new ChatPlay
            {
                User = ChatTurns.Clean(user),
                Login = login,
                Moderator = moderator,
                Text = body,
                Events = events,
                EndMs = endMs,
            });
        }

        /// <summary>The text without the spaces, combining marks and format
        /// characters at its end, nor anything from Unicode's tag and
        /// variation block (U+E0000 to U+E0FFF), which draws nothing
        /// either.</summary>
        internal static string TrimInvisibleEnd(string text)
        {
            int end = text.Length;
            while (end > 0)
            {
                char c = text[end - 1];
                if (char.IsWhiteSpace(c)
                    || char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format)
                {
                    end--;
                    continue;
                }
                if (end >= 2 && char.IsSurrogatePair(text[end - 2], c)
                    && char.ConvertToUtf32(text[end - 2], c) is >= 0xE0000 and <= 0xE0FFF)
                {
                    end -= 2;
                    continue;
                }
                break;
            }
            return end == text.Length ? text : text.Substring(0, end);
        }

        /// <summary>The streamer's Pause: stops the command playing and clears
        /// the line.</summary>
        internal void StopAll() => _player.Skip(all: true);

        internal bool SkipCurrent() => _player.Skip(all: false);

        internal void ClearLine() => _player.ClearWaiting();

        internal void EndClaim() => _turns.EndClaim();

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            lock (_stateLock)
            {
                var dst = _statePool.Next();
                _player.CopyButtons(dst.Buttons);
                return dst;
            }
        }

        /// <summary>Test seam: a row that reads without a socket.</summary>
        internal void AttachForTest() => _attached = true;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _attached = false;
            if (_client != null)
            {
                _client.Message -= OnMessage;
                _client.Dispose();
            }
            _player.Dispose();
            TwitchChatRuntime.ClearLive(this);
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
