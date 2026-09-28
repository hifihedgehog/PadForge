using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>What a libhmk keyboard says about itself in its metadata.</summary>
    public sealed class LibhmkMetadata
    {
        public string Name { get; init; } = string.Empty;
        public int NumKeys { get; init; }
        public int NumProfiles { get; init; }
        public int NumLayers { get; init; }
    }

    /// <summary>
    /// Keyboards running libhmk, the open Hall-effect firmware
    /// (github.com/peppapighs/libhmk, commit ad426f0), over its raw HID
    /// command channel, the way its web configurator hmkconf
    /// (github.com/peppapighs/hmkconf) talks to it.
    ///
    /// <para>The channel is the collection on usage page 0xFFAB, usage 0xAB,
    /// with 64-byte input and output reports and no report ID
    /// (usb_descriptors.h:103-107, usb_descriptors.c:89-105). A request is
    /// the command ID in byte 0 and its parameters after it, 64 bytes in all
    /// (hmkconf commander.ts:56-66). The firmware answers each with the
    /// command ID echoed in byte 0, or 255 when the command failed
    /// (commands.c:479-480), and drops a request that arrives while another
    /// is still being answered (commands.c:169-180). The answer carries no
    /// other tag, so the route opens the collection exclusively: another
    /// program's answers would be indistinguishable from this route's.</para>
    ///
    /// <para>Depth is <c>COMMAND_ANALOG_INFO</c> (5) with the first key index
    /// in byte 1, answered with 21 packed entries of a little-endian u16 ADC
    /// value and a u8 distance (commands.h:71-73, 160-163 and 183,
    /// commands.c:215-227). The distance is 0 to 255 through the firmware's
    /// log lookup table (distance.h:24-33 and 119-136). Entries past the key
    /// count are not written and hold whatever the last answer left there,
    /// so only the entries below the count are read (hmkconf
    /// analog-info.ts:36).</para>
    ///
    /// <para>The key count comes from the keyboard's own metadata,
    /// <c>COMMAND_GET_METADATA</c> (13): gzip-compressed JSON that libhmk's
    /// build embeds with <c>numKeys</c> among its fields
    /// (scripts/metadata.py:50-73, commands.c:287-296), read 59 bytes at a
    /// time as hmkconf reads it (metadata.ts:23-68). The keyboard.json files
    /// cannot stand in for it: HE60 and HE60 v2 share 0xAB60 and have 67
    /// and 69 keys (keyboards/he60/keyboard.json:7-13,
    /// keyboards/he60-v2/keyboard.json:7-13).</para>
    /// </summary>
    public static class LibhmkProtocol
    {
        public const ushort VendorId = 0xAB50;

        /// <summary>The product IDs of the keyboards in libhmk's repository:
        /// HE16, HE60 and HE60 v2, M256-WHE (keyboards/*/keyboard.json:6-7).</summary>
        public static readonly ushort[] ProductIds = { 0xAB16, 0xAB60, 0xAB65 };

        public const ushort UsagePage = 0xFFAB;
        public const ushort Usage = 0x00AB;

        /// <summary>RAW_HID_EP_SIZE (usb_descriptors.h:103).</summary>
        public const int PayloadLength = 64;

        /// <summary>The Windows report length: the payload and the report ID
        /// byte, 0 for this unnumbered collection.</summary>
        public const int ReportLength = PayloadLength + 1;

        // command_id_t, commands.h:26-60, and HMK_Command in hmkconf
        // commands/index.ts:19-53.
        public const byte CommandFirmwareVersion = 0;
        public const byte CommandAnalogInfo = 5;
        public const byte CommandGetProfile = 8;
        public const byte CommandGetMetadata = 13;
        public const byte CommandGetKeymap = 128;
        public const byte CommandUnknown = 255;

        /// <summary>The oldest firmware hmkconf accepts
        /// (HMK_FIRMWARE_MIN_VERSION, hmkconf libhmk/index.ts:19,
        /// hmk-keyboard.svelte.ts:241-246). Every command this route sends
        /// has the same number in each version hmkconf supports.</summary>
        public const ushort MinFirmwareVersion = 0x0104;

        /// <summary>Entries per answer: ANALOG_INFO_MAX_ENTRIES,
        /// GET_KEYMAP_MAX_ENTRIES and GET_METADATA_MAX_ENTRIES (hmkconf
        /// analog-info.ts:21, keymap.ts:22, metadata.ts:23, and the
        /// commands.h:160-196 layouts they follow).</summary>
        public const int AnalogInfoEntries = 21;
        public const int KeymapEntries = 63;
        public const int MetadataChunk = 59;

        /// <summary>HMK_MAX_NUM_KEYS, _PROFILES and _LAYERS (hmkconf
        /// libhmk/index.ts:32-34, common.h:68-83).</summary>
        public const int MaxKeys = 256;
        public const int MaxProfiles = 8;
        public const int MaxLayers = 8;

        /// <summary>The smallest actuation point hmkconf lets a key have,
        /// HMK_MIN_DISTANCE (libhmk/index.ts:39, distance.ts:21-26). A
        /// distance below it counts as rest, so sensor noise on idle keys
        /// never fills the key state.</summary>
        public const int MinDistance = 4;

        /// <summary>Full scale of a distance (HMK_MAX_DISTANCE,
        /// libhmk/index.ts:40).</summary>
        public const int MaxDistance = 255;

        /// <summary>Upper bounds on the metadata, far above any keyboard's
        /// (a few kilobytes), so a corrupt length cannot loop forever.</summary>
        public const int MaxMetadataBytes = 64 * 1024;
        public const int MaxMetadataJsonBytes = 1024 * 1024;

        /// <summary>MO(layer), SP_MO_MIN to SP_MO_MAX (keycodes.h:193-194).</summary>
        public const byte MomentaryLayerFirst = 0xC0;
        public const byte MomentaryLayerLast = 0xC7;

        /// <summary>The keycode to key code table in the data file.</summary>
        public const string KeycodeTableName = "libhmk_keycodes";

        /// <summary>The first code of PadForge's vendor range, for keys with
        /// no key code of their own.</summary>
        public const int VendorCodeBase = 0x600;

        public static bool IsProductId(ushort productId) => Array.IndexOf(ProductIds, productId) >= 0;

        /// <summary>The raw HID collection of a libhmk keyboard in the
        /// repository: VID, PID, usage page and usage (hmkconf filters on the
        /// same usage pair, hmk-keyboard.svelte.ts:217-231), 64-byte reports
        /// both ways and no report ID.</summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
            => info != null
               && info.VendorId == VendorId
               && IsProductId(info.ProductId)
               && info.UsagePage == UsagePage
               && info.Usage == Usage
               && info.InputReportLength == ReportLength
               && info.OutputReportLength == ReportLength
               && info.HasInputReport(0)
               && info.HasOutputReport(0);

        /// <summary>One request, report ID 0 first: the command, then its
        /// parameters, zeros to 64 payload bytes.</summary>
        public static byte[] Request(byte command, params byte[] parameters)
        {
            var r = new byte[ReportLength];
            r[1] = command;
            if (parameters != null)
                Array.Copy(parameters, 0, r, 2, Math.Min(parameters.Length, PayloadLength - 1));
            return r;
        }

        public static byte[] FirmwareVersionRequest() => Request(CommandFirmwareVersion);

        /// <summary>GET_METADATA with its u32 offset, little-endian
        /// (command_in_metadata_t, commands.h:88-90, hmkconf metadata.ts:28-33).</summary>
        public static byte[] MetadataRequest(int offset)
            => Request(CommandGetMetadata, (byte)offset, (byte)(offset >> 8), (byte)(offset >> 16), (byte)(offset >> 24));

        public static byte[] ProfileRequest() => Request(CommandGetProfile);

        /// <summary>GET_KEYMAP with profile, layer and first key
        /// (command_in_keymap_t, commands.h:92-98). hmkconf sends those three
        /// and no length (keymap.ts:33-37), as the firmware reads none
        /// (commands.c:274-286).</summary>
        public static byte[] KeymapRequest(int profile, int layer, int offset)
            => Request(CommandGetKeymap, (byte)profile, (byte)layer, (byte)offset);

        public static byte[] AnalogInfoRequest(int offset) => Request(CommandAnalogInfo, (byte)offset);

        /// <summary>Depth for a distance: rest below <see cref="MinDistance"/>,
        /// otherwise the distance over 255.</summary>
        public static float Depth(int distance)
            => distance < MinDistance ? 0f : Math.Min(distance, MaxDistance) / (float)MaxDistance;

        /// <summary>Reads the gzip-compressed metadata JSON. False when it is
        /// not gzip, not JSON, or lacks a field hmkconf's schema requires, or
        /// a field is out of hmkconf's range (keyboard/metadata.ts:96-118).</summary>
        public static bool TryParseMetadata(ReadOnlySpan<byte> compressed, out LibhmkMetadata metadata)
        {
            metadata = null;
            try
            {
                using var input = new MemoryStream(compressed.ToArray());
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var json = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (json.Length + read > MaxMetadataJsonBytes) return false;
                    json.Write(chunk, 0, read);
                }
                using var doc = JsonDocument.Parse(json.ToArray());
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return false;
                if (!TryInt(root, "numKeys", 1, MaxKeys, out int keys)) return false;
                if (!TryInt(root, "numProfiles", 1, MaxProfiles, out int profiles)) return false;
                if (!TryInt(root, "numLayers", 1, MaxLayers, out int layers)) return false;
                metadata = new LibhmkMetadata
                {
                    Name = name.GetString() ?? string.Empty,
                    NumKeys = keys,
                    NumProfiles = profiles,
                    NumLayers = layers,
                };
                return true;
            }
            catch (Exception e) when (e is InvalidDataException or JsonException or IOException)
            {
                return false;
            }
        }

        private static bool TryInt(JsonElement root, string name, int min, int max, out int value)
        {
            value = 0;
            return root.TryGetProperty(name, out var element)
                   && element.ValueKind == JsonValueKind.Number
                   && element.TryGetInt32(out value)
                   && value >= min && value <= max;
        }

        /// <summary>The key code of each key, by key index, from the layer 0
        /// keycodes of the active profile. A keycode takes the HID usage
        /// libhmk's keycode_to_hid gives it (keycodes.c:20-191, through the
        /// data file's table). The first key holding MO(n), the layer key,
        /// takes Fn. Every other key without a code of its own, and every key
        /// whose code an earlier key already took, takes 0x600 plus its
        /// index, so each key keeps a code of its own.</summary>
        public static int[] KeyCodes(ReadOnlySpan<byte> keymap, int[] keycodeTable)
        {
            var codes = new int[keymap.Length];
            var used = new HashSet<int>();
            for (int i = 0; i < keymap.Length; i++)
            {
                byte keycode = keymap[i];
                int code = keycodeTable != null && keycode < keycodeTable.Length ? keycodeTable[keycode] : 0;
                if (code == 0 && keycode >= MomentaryLayerFirst && keycode <= MomentaryLayerLast)
                    code = AnalogKeyCodes.Fn;
                if (code == 0 || !used.Add(code)) code = VendorCodeBase + i;
                codes[i] = code;
            }
            return codes;
        }
    }

    /// <summary>
    /// A libhmk conversation. The handshake follows hmkconf's connect and
    /// load: firmware version, metadata, active profile, then the layer 0
    /// keymap of that profile (hmk-keyboard.svelte.ts:240-249, profile.ts,
    /// keymap.ts). Each pass then reads every key's distance in groups of 21
    /// (analog-info.ts:23-45). Every command is a read, so
    /// <see cref="AnalogKeyboardSession.Stop"/> has nothing to undo.
    ///
    /// <para>hmkconf refetches the analog info 30 times a second for its
    /// display (analog-info-query.svelte.ts:20). A pass here runs as soon as
    /// the last one ends. Each exchange waits for the keyboard's answer on
    /// its next USB poll, so the keyboard sets the pace.</para>
    /// </summary>
    public sealed class LibhmkSession : AnalogKeyboardSession
    {
        private enum Reply
        {
            Answered,
            Refused,
            TimedOut,
            Failed,
        }

        private readonly int[] _keycodeTable;
        private LibhmkMetadata _metadata;
        private int[] _codes;
        private byte[] _distance;

        public LibhmkSession()
        {
            _keycodeTable = AnalogKeyboardData.Table(OtherRoutes.DataFile, LibhmkProtocol.KeycodeTableName);
        }

        /// <summary>The firmware version the handshake read.</summary>
        public ushort FirmwareVersion { get; private set; }

        /// <summary>The active profile whose keymap named the keys.</summary>
        public int Profile { get; private set; } = -1;

        public LibhmkMetadata Metadata => _metadata;

        /// <summary>Each key's code by key index, once the handshake ran.</summary>
        public int[] Codes => _codes;

        public override string ModelName => string.IsNullOrWhiteSpace(_metadata?.Name) ? null : _metadata.Name;

        public override int[] KeyOrder => _codes == null ? null : AnalogKeyboardData.KeysOf(_codes);

        public override bool Start(IAnalogKeyboardTransport io)
        {
            if (Exchange(io, LibhmkProtocol.FirmwareVersionRequest(), LibhmkProtocol.CommandFirmwareVersion)
                != Reply.Answered) return false;
            FirmwareVersion = (ushort)(Buffer[2] | (Buffer[3] << 8));
            if (FirmwareVersion < LibhmkProtocol.MinFirmwareVersion) return false;

            if (!ReadMetadata(io, out var metadata)) return false;

            if (Exchange(io, LibhmkProtocol.ProfileRequest(), LibhmkProtocol.CommandGetProfile) != Reply.Answered)
                return false;
            int profile = Buffer[2];
            if (profile >= metadata.NumProfiles) return false;

            var keymap = new byte[metadata.NumKeys];
            for (int offset = 0; offset < keymap.Length; offset += LibhmkProtocol.KeymapEntries)
            {
                if (Exchange(io, LibhmkProtocol.KeymapRequest(profile, 0, offset), LibhmkProtocol.CommandGetKeymap)
                    != Reply.Answered) return false;
                int count = Math.Min(LibhmkProtocol.KeymapEntries, keymap.Length - offset);
                Array.Copy(Buffer, 2, keymap, offset, count);
            }

            _metadata = metadata;
            Profile = profile;
            _codes = LibhmkProtocol.KeyCodes(keymap, _keycodeTable);
            _distance = new byte[metadata.NumKeys];
            return true;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (_codes == null) return AnalogPollResult.Failed;
            int keys = _codes.Length;
            for (int offset = 0; offset < keys; offset += LibhmkProtocol.AnalogInfoEntries)
            {
                switch (Exchange(io, LibhmkProtocol.AnalogInfoRequest(offset), LibhmkProtocol.CommandAnalogInfo))
                {
                    case Reply.TimedOut:
                        return AnalogPollResult.NoAnswer;
                    case Reply.Refused:
                        // The firmware refuses only an offset past its key
                        // count (commands.c:219), so the handshake's count is
                        // wrong and no later pass can be right.
                    case Reply.Failed:
                        return AnalogPollResult.Failed;
                }
                int count = Math.Min(LibhmkProtocol.AnalogInfoEntries, keys - offset);
                for (int j = 0; j < count; j++)
                    _distance[offset + j] = Buffer[2 + 3 * j + 2];
            }

            output.ResetForReuse();
            for (int i = 0; i < keys; i++)
            {
                float depth = LibhmkProtocol.Depth(_distance[i]);
                if (depth > 0f) output.Set(_codes[i], depth);
            }
            return AnalogPollResult.Ok;
        }

        /// <summary>GET_METADATA from offset 0 until the remaining length fits
        /// one answer, as hmkconf reads it (metadata.ts:25-45): the answer
        /// holds the bytes left from the offset as a little-endian u32 in
        /// bytes 1 to 4 and up to 59 of them after it (commands.h:165-168,
        /// commands.c:287-296).</summary>
        private bool ReadMetadata(IAnalogKeyboardTransport io, out LibhmkMetadata metadata)
        {
            metadata = null;
            var compressed = new List<byte>();
            while (true)
            {
                if (Exchange(io, LibhmkProtocol.MetadataRequest(compressed.Count), LibhmkProtocol.CommandGetMetadata)
                    != Reply.Answered) return false;
                uint left = (uint)(Buffer[2] | (Buffer[3] << 8) | (Buffer[4] << 16) | (Buffer[5] << 24));
                if (left == 0) return false;
                int take = (int)Math.Min(left, (uint)LibhmkProtocol.MetadataChunk);
                for (int i = 0; i < take; i++) compressed.Add(Buffer[6 + i]);
                if (left <= LibhmkProtocol.MetadataChunk) break;
                if (compressed.Count >= LibhmkProtocol.MaxMetadataBytes) return false;
            }
            return LibhmkProtocol.TryParseMetadata(compressed.ToArray(), out metadata);
        }

        /// <summary>hmkconf's command timeout, the longest an answer is
        /// waited for (sendCommand's default, commander.ts:48).</summary>
        public const int CommandTimeoutMs = 4000;

        private bool _lateAnswer;
        private byte _lateCommand;
        private long _lateUntil;

        /// <summary>Sends one request and waits for the answer that echoes
        /// its command, skipping any other report, the way hmkconf's
        /// Commander matches byte 0 (commander.ts:69-77). An answer of 255 is
        /// a refusal. The answer is left in <see cref="AnalogKeyboardSession.Buffer"/>
        /// with the report ID in byte 0 and the echo in byte 1.
        ///
        /// <para>An answer names only its command (commands.c:479-480), so the
        /// answer to a request that timed out could pass for the next
        /// request's, the next offset's depths landing on this offset's keys.
        /// After a timeout the next exchange first waits out that answer,
        /// until hmkconf's timeout from the late request runs out. The pass
        /// that timed out returns at once, so its keys release while the
        /// keyboard is stalled.</para></summary>
        private Reply Exchange(IAnalogKeyboardTransport io, byte[] request, byte command)
        {
            if (_lateAnswer && !AwaitLateAnswer(io)) return Reply.Failed;
            io.DiscardStale();
            if (!io.Send(request)) return Reply.Failed;
            long sent = Environment.TickCount64;
            long deadline = sent + AnswerTimeoutMs;
            while (true)
            {
                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) return Late(command, sent);
                int n = io.Receive(Buffer, remaining);
                if (n < 0) return Reply.Failed;
                if (n == 0) return Late(command, sent);
                // hmkconf takes only 64-byte answers (commander.ts:27-33).
                if (n < LibhmkProtocol.ReportLength || Buffer[0] != 0) continue;
                if (Buffer[1] == command) return Reply.Answered;
                if (Buffer[1] == LibhmkProtocol.CommandUnknown) return Reply.Refused;
            }
        }

        private Reply Late(byte command, long sent)
        {
            _lateAnswer = true;
            _lateCommand = command;
            _lateUntil = sent + CommandTimeoutMs;
            return Reply.TimedOut;
        }

        /// <summary>Reads until the late answer arrives, or a refusal, or the
        /// timeout. False when the device is gone.</summary>
        private bool AwaitLateAnswer(IAnalogKeyboardTransport io)
        {
            while (true)
            {
                int remaining = (int)(_lateUntil - Environment.TickCount64);
                if (remaining <= 0) break;
                int n = io.Receive(Buffer, remaining);
                if (n < 0) return false;
                if (n == 0) break;
                if (n < LibhmkProtocol.ReportLength || Buffer[0] != 0) continue;
                if (Buffer[1] == _lateCommand || Buffer[1] == LibhmkProtocol.CommandUnknown) break;
            }
            _lateAnswer = false;
            return true;
        }
    }
}
