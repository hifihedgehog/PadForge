using System;
using System.Collections.Generic;

namespace PadForge.Engine.Flydigi
{
    /// <summary>How a picture is fitted to the screen.</summary>
    public enum FlydigiScreenFit
    {
        /// <summary>Covers the screen, and what does not fit is cut off.</summary>
        Fill = 0,
        /// <summary>Shows the whole picture, with black bars where it is
        /// narrower or shorter than the screen.</summary>
        Fit = 1,
        /// <summary>Takes the screen's shape, whatever the picture's.</summary>
        Stretch = 2,
    }

    /// <summary>
    /// The picture on the 160 by 80 color screen of a Flydigi Apex 4, Apex 5
    /// and Apex 6 Pro (#586). A frame is an LVGL v8 image of 25,604 bytes:
    /// the header 04 80 02 0A, then 160 by 80 RGB565 pixels, high byte
    /// first, row by row (openflydigi PROTOCOL.md section 8a,
    /// flydigi/screen.py, flydigi-space-station-mac docs/protocol.md section
    /// 6 and Screen.swift). A picture of several frames is the frames one
    /// after another.
    /// </summary>
    public static class FlydigiScreenFormat
    {
        public const int Width = 160;
        public const int Height = 80;
        public const int HeaderLength = 4;
        public const int FrameLength = HeaderLength + Width * Height * 2;

        /// <summary>cf 4, LV_IMG_CF_TRUE_COLOR, width 160 and height 80
        /// packed into a little-endian word (screen.py frame_header).</summary>
        public static readonly byte[] Header = { 0x04, 0x80, 0x02, 0x0A };

        /// <summary>One frame from 160 by 80 RGB pixels, three bytes each,
        /// row by row. Each channel is rounded to its bits the way Flydigi's
        /// converter rounds it, R/8 and B/8 and G/4 to the nearest, capped
        /// (Screen.swift encodeFrame, docs/protocol.md section 6).</summary>
        public static byte[] EncodeFrame(ReadOnlySpan<byte> rgb)
        {
            if (rgb.Length != Width * Height * 3)
                throw new ArgumentException("A frame takes 160 by 80 RGB pixels.", nameof(rgb));
            var frame = new byte[FrameLength];
            Header.CopyTo(frame, 0);
            for (int pixel = 0, at = HeaderLength; pixel < Width * Height; pixel++, at += 2)
            {
                int r5 = Math.Min(31, (rgb[pixel * 3] + 4) / 8);
                int g6 = Math.Min(63, (rgb[pixel * 3 + 1] + 2) / 4);
                int b5 = Math.Min(31, (rgb[pixel * 3 + 2] + 4) / 8);
                int value = (r5 << 11) | (g6 << 5) | b5;
                frame[at] = (byte)(value >> 8);
                frame[at + 1] = (byte)value;
            }
            return frame;
        }

        /// <summary>Whether <paramref name="frame"/> is a frame of this
        /// screen: its length and its header.</summary>
        public static bool IsFrame(ReadOnlySpan<byte> frame)
            => frame.Length == FrameLength && frame.Slice(0, HeaderLength).SequenceEqual(Header);

        /// <summary>A frame's pixels as RGB, the top bits of each channel
        /// copied into its low ones, so white comes back white
        /// (screen.py unpack_pixel). For the preview, which shows what the
        /// pad gets.</summary>
        public static byte[] DecodeFrame(ReadOnlySpan<byte> frame)
        {
            if (!IsFrame(frame)) throw new ArgumentException("Not a 160 by 80 screen frame.", nameof(frame));
            var rgb = new byte[Width * Height * 3];
            for (int pixel = 0, at = HeaderLength; pixel < Width * Height; pixel++, at += 2)
            {
                int value = (frame[at] << 8) | frame[at + 1];
                int r = (value >> 11) & 0x1F, g = (value >> 5) & 0x3F, b = value & 0x1F;
                rgb[pixel * 3] = (byte)((r << 3) | (r >> 2));
                rgb[pixel * 3 + 1] = (byte)((g << 2) | (g >> 4));
                rgb[pixel * 3 + 2] = (byte)((b << 3) | (b >> 2));
            }
            return rgb;
        }

        /// <summary>A picture of <paramref name="width"/> by
        /// <paramref name="height"/> RGB pixels brought to the screen's 160
        /// by 80 by <paramref name="fit"/>: each screen pixel the average of
        /// the picture's pixels under it, and the nearest one where the
        /// picture is smaller than the screen (screen.py fit). Where the
        /// picture does not reach, the screen is black.</summary>
        public static byte[] Fit(ReadOnlySpan<byte> rgb, int width, int height, FlydigiScreenFit fit)
        {
            if (width <= 0 || height <= 0 || rgb.Length != width * height * 3)
                throw new ArgumentException("The picture's size and its pixels disagree.", nameof(rgb));
            int srcX = 0, srcY = 0, srcW = width, srcH = height;
            int dstX = 0, dstY = 0, dstW = Width, dstH = Height;
            if (fit == FlydigiScreenFit.Fill)
            {
                double scale = Math.Max((double)Width / width, (double)Height / height);
                srcW = Math.Min(width, (int)Math.Round(Width / scale, MidpointRounding.ToEven));
                srcH = Math.Min(height, (int)Math.Round(Height / scale, MidpointRounding.ToEven));
                srcW = Math.Max(1, srcW);
                srcH = Math.Max(1, srcH);
                srcX = (width - srcW) / 2;
                srcY = (height - srcH) / 2;
            }
            else if (fit == FlydigiScreenFit.Fit)
            {
                double scale = Math.Min((double)Width / width, (double)Height / height);
                dstW = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.ToEven));
                dstH = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.ToEven));
                dstX = (Width - dstW) / 2;
                dstY = (Height - dstH) / 2;
            }

            var output = new byte[Width * Height * 3];
            for (int y = 0; y < dstH; y++)
            {
                int y0 = srcY + y * srcH / dstH;
                int y1 = Math.Max(y0 + 1, srcY + (y + 1) * srcH / dstH);
                for (int x = 0; x < dstW; x++)
                {
                    int x0 = srcX + x * srcW / dstW;
                    int x1 = Math.Max(x0 + 1, srcX + (x + 1) * srcW / dstW);
                    int red = 0, green = 0, blue = 0, count = 0;
                    for (int sy = y0; sy < y1; sy++)
                    {
                        int row = (sy * width + x0) * 3;
                        for (int sx = x0; sx < x1; sx++, row += 3)
                        {
                            red += rgb[row];
                            green += rgb[row + 1];
                            blue += rgb[row + 2];
                            count++;
                        }
                    }
                    int at = ((dstY + y) * Width + dstX + x) * 3;
                    output[at] = (byte)(red / count);
                    output[at + 1] = (byte)(green / count);
                    output[at + 2] = (byte)(blue / count);
                }
            }
            return output;
        }

        /// <summary>The frames one after another, the picture the pad
        /// takes.</summary>
        public static byte[] Join(IReadOnlyList<byte[]> frames)
        {
            var data = new byte[frames.Count * FrameLength];
            for (int i = 0; i < frames.Count; i++) frames[i].CopyTo(data, i * FrameLength);
            return data;
        }
    }

    /// <summary>
    /// The screen chip's serial bootloader on an Apex 5 and an Apex 6 Pro
    /// (#586): what openflydigi's flydigi/screen_ota.py sends and reads at
    /// 921600 8N1 once HID command 31 has added a USB serial port,
    /// FFAA:5555, beside the gamepad (PROTOCOL.md section 8d, run on a wired
    /// Apex 5). A packet is the opcode, a little-endian length and the
    /// payload. A reply is the result, the opcode, a length and the payload.
    ///
    /// <para>The Apex 5 takes the older writer's packets. The Apex 6 Pro
    /// takes the newer engine's, which the reference names strict: each
    /// write declares its true length, the frame rate is rounded another
    /// way, and every reply is checked against what was sent
    /// (gpa6_screen.py, screen_ota.upload with strict).</para>
    /// </summary>
    public static class FlydigiScreenOta
    {
        public const byte OpErase = 3;
        public const byte OpWrite = 5;
        public const byte OpGetVersion = 10;
        public const byte OpGetBase = 11;
        public const byte OpReset = 12;

        /// <summary>ScreenUpgradePicType: one frame is a PNG, more are a
        /// GIF, whatever the file was.</summary>
        public const byte PictureGif = 1;
        public const byte PicturePng = 2;

        public const int EraseBlock = 4096;
        public const int WriteChunk = 55;

        public const ushort VendorId = 0xFFAA;
        public const ushort ProductId = 0x5555;
        public const int BaudRate = 921600;

        /// <summary>The most frames each pad takes: the one-byte count, and
        /// the 150 Flydigi's current editor allows for the Apex 6
        /// (validate_frames, gpa6_screen.MAX_FRAMES).</summary>
        public const int Apex5MaximumFrames = 255;
        public const int Apex6MaximumFrames = 150;

        /// <summary>How long a reply is waited for: their timer's sixty
        /// ticks of 300 ms (REPLY_TIMEOUT), and the newer engine's 3 s a
        /// try.</summary>
        public const int ReplyTimeoutMs = 18_000;
        public const int StrictReplyMs = 3000;
        public const int StrictRetries = 3;

        /// <summary>How long after command 31 the port is looked for, and
        /// how long it is looked for (SWITCH_SETTLE, PORT_TIMEOUT).</summary>
        public const int SwitchSettleMs = 5000;
        public const int PortTimeoutMs = 30_000;

        /// <summary>The length field each opcode carries, copied and not
        /// computed: three of the five disagree with their own payloads
        /// (LENGTH_FIELD).</summary>
        public static ushort LengthField(byte opcode) => opcode switch
        {
            OpGetBase => 4,
            OpGetVersion => 6,
            OpErase => 6,
            OpWrite => 64,
            OpReset => 8,
            _ => 0,
        };

        /// <summary>The bytes a reply to each opcode takes in the newer
        /// engine, a refusal two (_exchange_checked reply_sizes).</summary>
        public static int ReplyLength(byte opcode) => opcode switch
        {
            OpGetBase => 8,
            OpGetVersion => 8,
            OpErase => 8,
            OpWrite => 10,
            OpReset => 2,
            _ => 0,
        };

        /// <summary>Flydigi's own CRC-32, AppOtasCrcCal: the reflected table,
        /// indexed by bits 8 to 15 of the running value, which is a C# int,
        /// so the division truncates toward zero and the shift drops its top
        /// bits (screen_ota.checksum).</summary>
        public static uint Checksum(ReadOnlySpan<byte> data)
        {
            int crc = 0;
            foreach (byte b in data)
            {
                int quotient = crc / 256;
                crc = unchecked((crc << 8) ^ (int)Table[(quotient ^ b) & 0xFF]);
            }
            return unchecked((uint)crc);
        }

        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
                table[i] = value;
            }
            return table;
        }

        /// <summary>A packet: the opcode, the length field and the payload.
        /// The newer engine's write declares the payload's length plus three
        /// (_exchange_checked), every other packet the copied field
        /// (build).</summary>
        public static byte[] Packet(byte opcode, ReadOnlySpan<byte> payload, bool strict)
        {
            ushort length = strict && opcode == OpWrite ? (ushort)(3 + payload.Length) : LengthField(opcode);
            var packet = new byte[3 + payload.Length];
            packet[0] = opcode;
            packet[1] = (byte)length;
            packet[2] = (byte)(length >> 8);
            payload.CopyTo(packet.AsSpan(3));
            return packet;
        }

        /// <summary>A picture of one frame is a PNG, of more a GIF
        /// (picture_type).</summary>
        public static byte PictureType(int frames) => frames == 1 ? PicturePng : PictureGif;

        /// <summary>The frame time in hundredths of a second: rounded to the
        /// nearest for the Apex 5 (frame_rate), half up for the Apex 6
        /// (upload with strict), 1 to 255.</summary>
        public static byte FrameRate(int intervalMs, bool strict)
            => strict
                ? (byte)Math.Clamp((intervalMs + 5) / 10, 1, 255)
                : (byte)Math.Clamp((int)Math.Round(intervalMs / 10.0, MidpointRounding.ToEven), 1, 255);

        /// <summary>PicGetBaseAddr's payload: the picture type, the frame
        /// count, the frame time and 0, since PadForge never asks for the
        /// factory picture back.</summary>
        public static byte[] BasePayload(int frames, int intervalMs, bool strict)
            => new[] { PictureType(frames), (byte)frames, FrameRate(intervalMs, strict), (byte)0 };

        public static byte[] AddressPayload(uint address)
            => new[] { (byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24) };

        /// <summary>WriteData's payload: the address, the length the write
        /// declares, then the bytes. The older writer declares 55 whatever
        /// the last write's length.</summary>
        public static byte[] WritePayload(uint address, ReadOnlySpan<byte> chunk, bool strict)
        {
            var payload = new byte[6 + chunk.Length];
            AddressPayload(address).CopyTo(payload, 0);
            ushort declared = strict ? (ushort)chunk.Length : (ushort)WriteChunk;
            payload[4] = (byte)declared;
            payload[5] = (byte)(declared >> 8);
            chunk.CopyTo(payload.AsSpan(6));
            return payload;
        }

        /// <summary>PicResetDevice's payload: the picture's length and its
        /// checksum.</summary>
        public static byte[] ResetPayload(ReadOnlySpan<byte> data)
        {
            var payload = new byte[8];
            int length = data.Length;
            uint crc = Checksum(data);
            payload[0] = (byte)length; payload[1] = (byte)(length >> 8); payload[2] = (byte)(length >> 16); payload[3] = (byte)(length >> 24);
            payload[4] = (byte)crc; payload[5] = (byte)(crc >> 8); payload[6] = (byte)(crc >> 16); payload[7] = (byte)(crc >> 24);
            return payload;
        }

        public static int Erases(int length) => (length + EraseBlock - 1) / EraseBlock;

        public static int Writes(int length) => (length + WriteChunk - 1) / WriteChunk;

        /// <summary>An older reply: the result, the opcode and the payload
        /// from byte 4. A reply under five bytes carries the result and the
        /// opcode alone (parse).</summary>
        public static bool ParseLegacy(ReadOnlySpan<byte> reply, out byte result, out byte opcode, out byte[] payload)
        {
            result = opcode = 0;
            payload = Array.Empty<byte>();
            if (reply.Length < 2) return false;
            result = reply[0];
            opcode = reply[1];
            if (reply.Length >= 5) payload = reply.Slice(4).ToArray();
            return true;
        }

        /// <summary>What the bytes read so far say about a newer exchange
        /// (_exchange_checked).</summary>
        public enum StrictReading
        {
            /// <summary>More bytes are needed.</summary>
            More,
            /// <summary>The reply to the packet came, accepted.</summary>
            Accepted,
            /// <summary>The device refused the packet.</summary>
            Refused,
        }

        /// <summary>Reads the newer engine's replies out of
        /// <paramref name="buffered"/>, dropping each byte that cannot start
        /// one and each reply that answers another packet: a reply to the
        /// opcode sent with a nonzero result is a refusal, and an erase's or a
        /// write's reply must name the address, and the write's length,
        /// that were sent (_exchange_checked). <paramref name="buffered"/>
        /// keeps what is left.</summary>
        public static StrictReading ReadStrict(List<byte> buffered, byte opcode, ReadOnlySpan<byte> payload,
            out byte result, out byte[] body)
        {
            result = 0;
            body = Array.Empty<byte>();
            while (buffered.Count >= 2)
            {
                byte gotResult = buffered[0], got = buffered[1];
                int size = ReplyLength(got);
                if (size == 0)
                {
                    buffered.RemoveAt(0);
                    continue;
                }
                if (got == opcode && gotResult != 0)
                {
                    result = gotResult;
                    buffered.Clear();
                    return StrictReading.Refused;
                }
                if (gotResult != 0) size = 2;
                if (buffered.Count < size) return StrictReading.More;
                var reply = buffered.GetRange(0, size).ToArray();
                buffered.RemoveRange(0, size);
                if (got != opcode) continue;
                var replyBody = size >= 4 ? reply.AsSpan(4).ToArray() : Array.Empty<byte>();
                if (opcode == OpErase && !Matches(replyBody, payload, 4)) continue;
                if (opcode == OpWrite && !Matches(replyBody, payload, 6)) continue;
                result = gotResult;
                body = replyBody;
                return StrictReading.Accepted;
            }
            return StrictReading.More;
        }

        private static bool Matches(ReadOnlySpan<byte> body, ReadOnlySpan<byte> payload, int count)
            => body.Length >= count && payload.Length >= count && body.Slice(0, count).SequenceEqual(payload.Slice(0, count));
    }

    /// <summary>
    /// The Apex 4's screen in Xbox mode, 045E:028E, over interface 0's
    /// interrupt endpoints, OUT 0x05 and IN 0x81, by cable
    /// (flydigi-space-station-mac docs/protocol.md sections 1, 3 and 6,
    /// Framing.swift, Screen.swift, Replies.swift). In DInput mode the pad
    /// takes the start and then refuses every write until it is switched
    /// off, so this is the only way to its screen.
    /// </summary>
    public static class FlydigiApex4XinputScreen
    {
        public const byte Prefix = 0xA5;
        public const byte CmdDeviceInfo = 0x10;
        public const byte CmdSwitchToDInput = 0x17;
        public const byte CmdScreenStart = 0xD0;
        public const byte CmdScreenData = 0xD1;
        public const byte CmdScreenEnd = 0xD2;
        public const byte CmdScreenEndAll = 0xD3;

        /// <summary>DInput's command that turns the pad into an Xbox 360
        /// pad (DInput.Cmd.switchToXInput, 237).</summary>
        public const byte DInputSwitchToXInput = 0xED;

        public const byte OutEndpoint = 0x05;
        public const byte InEndpoint = 0x81;
        public const int CommandLength = 15;
        public const int Chunk = 26;
        public const int MaximumFrames = 35;
        public const int Attempts = 5;
        public const int DataReplyMs = 1500;
        public const int ReplyMs = 3000;

        /// <summary>A 15-byte command: A5, the command, its arguments, and
        /// the sum of the bytes before it at the end
        /// (XInput.command, Checksum.apply).</summary>
        public static byte[] Command(byte command, params byte[] args)
        {
            var packet = new byte[CommandLength];
            packet[0] = Prefix;
            packet[1] = command;
            if (args != null) Array.Copy(args, 0, packet, 2, Math.Min(args.Length, CommandLength - 3));
            byte sum = 0;
            for (int i = 0; i < CommandLength - 1; i++) sum += packet[i];
            packet[CommandLength - 1] = sum;
            return packet;
        }

        public static byte[] DeviceInfoRequest() => Command(CmdDeviceInfo);

        public static byte[] SwitchToDInput() => Command(CmdSwitchToDInput);

        /// <summary>The device type in a device-info reply, which rides at
        /// byte 14 of a 64-byte gamepad report: A5 10 at 14, the type at 16,
        /// the connection at 25 (XInputReply.deviceInfo). -1 for any other
        /// report.</summary>
        public static int ParseDeviceType(ReadOnlySpan<byte> report, out byte connection)
        {
            connection = 0;
            if (report.Length <= 26 || report[14] != Prefix || report[15] != CmdDeviceInfo) return -1;
            connection = report[25];
            return report[16];
        }

        /// <summary>A frame's start: A5 D0 09 01, the picture type 1, the
        /// frame count, the frame's number from 1, the period in 100 ms, the
        /// size high byte first, and the sum of bytes 1 to 9
        /// (ScreenUploadPlan.frameSteps).</summary>
        public static byte[] Start(int frame, int frames, byte period, int size)
        {
            var packet = new byte[CommandLength];
            packet[0] = Prefix;
            packet[1] = CmdScreenStart;
            packet[2] = 0x09;
            packet[3] = 0x01;
            packet[4] = 1;
            packet[5] = (byte)frames;
            packet[6] = (byte)frame;
            packet[7] = Math.Max((byte)1, period);
            packet[8] = (byte)(size >> 8);
            packet[9] = (byte)size;
            packet[10] = Sum(packet, 1, 9);
            return packet;
        }

        /// <summary>A data packet of 31 bytes: A5 D1, the offset high byte
        /// first, 26 bytes of the frame padded with FF, and the sum of
        /// bytes 1 to 29.</summary>
        public static byte[] Data(int offset, ReadOnlySpan<byte> chunk)
        {
            var packet = new byte[4 + Chunk + 1];
            packet[0] = Prefix;
            packet[1] = CmdScreenData;
            packet[2] = (byte)(offset >> 8);
            packet[3] = (byte)offset;
            packet.AsSpan(4, Chunk).Fill(0xFF);
            chunk.Slice(0, Math.Min(chunk.Length, Chunk)).CopyTo(packet.AsSpan(4));
            packet[^1] = Sum(packet, 1, packet.Length - 2);
            return packet;
        }

        /// <summary>A frame's end: A5 D2 07 01, the frame's number, the size
        /// high byte first, 0, and the sum of bytes 1 to 7.</summary>
        public static byte[] End(int frame, int size)
        {
            var packet = new byte[CommandLength];
            packet[0] = Prefix;
            packet[1] = CmdScreenEnd;
            packet[2] = 0x07;
            packet[3] = 0x01;
            packet[4] = (byte)frame;
            packet[5] = (byte)(size >> 8);
            packet[6] = (byte)size;
            packet[8] = Sum(packet, 1, 7);
            return packet;
        }

        /// <summary>The picture's end: A5 D3 07 01 and the frame count. The
        /// pad answers it with D2 (endAllStep).</summary>
        public static byte[] EndAll(int frames)
        {
            var packet = new byte[CommandLength];
            packet[0] = Prefix;
            packet[1] = CmdScreenEndAll;
            packet[2] = 0x07;
            packet[3] = 0x01;
            packet[4] = (byte)frames;
            packet[8] = Sum(packet, 1, 7);
            return packet;
        }

        /// <summary>A screen reply at byte 14 of a gamepad report: 5A A5, the
        /// command D0 to D3, the result at 18, 0 for done and 1 for send
        /// again (XInputReply.screenAck). False for any other report.</summary>
        public static bool ParseAck(ReadOnlySpan<byte> report, out byte command, out byte result)
        {
            command = result = 0;
            if (report.Length <= 20 || report[14] != 0x5A || report[15] != 0xA5
                || report[16] < CmdScreenStart || report[16] > CmdScreenEndAll)
                return false;
            command = report[16];
            result = report[18];
            return true;
        }

        /// <summary>The frame period in tenths of a second, 1 to 255
        /// (docs/protocol.md section 6).</summary>
        public static byte Period(int intervalMs)
            => (byte)Math.Clamp((int)Math.Round(intervalMs / 100.0, MidpointRounding.AwayFromZero), 1, 255);

        private static byte Sum(byte[] packet, int first, int last)
        {
            byte sum = 0;
            for (int i = first; i <= last; i++) sum += packet[i];
            return sum;
        }
    }
}
