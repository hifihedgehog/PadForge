using System;

namespace PadForge.Engine.Common.BlissBox
{
    /// <summary>
    /// The N64 Controller Pak over a Bliss-Box's native channel (issue #469).
    /// Each message is the Joybus command the pak takes, and each reply the
    /// bytes it answers.
    ///
    /// <para>The frames and both checksums are the API Tool's (memManager.cs
    /// memManagerLoadData, getN64Data, writeToPort, getCrc5 and getCrc8), and
    /// the checksums give the same values as libdragon's
    /// joybus_accessory_calculate_addr_checksum and
    /// joybus_accessory_calculate_data_crc. libdragon settles two points the
    /// tool leaves open: a status of 3 also means a pak is present (it changed
    /// since the last status), and a data CRC that comes back inverted means
    /// no pak answered.</para>
    /// </summary>
    public static class BlissBoxControllerPak
    {
        public const int BlockBytes = 32;
        public const int Blocks = 1024;
        public const int PakBytes = Blocks * BlockBytes;

        public const byte CommandStatus = 0x00;
        public const byte CommandRead = 0x02;
        public const byte CommandWrite = 0x03;

        /// <summary>A backup reads a block this many times at most before it
        /// gives up. The API Tool reads once and colors a bad block red.</summary>
        public const int ReadAttempts = 3;

        /// <summary>A restore gives up after this many rejected writes, the
        /// API Tool's limit ("too many CRC errors").</summary>
        public const int WriteErrorLimit = 16;

        /// <summary>The first data byte and CRC a Rumble Pak answers a read
        /// of block 0 with, the API Tool's "Looks like a rumble pack!" test.</summary>
        public const byte RumblePakFill = 0x80;
        public const byte RumblePakCrc = 0xB8;

        /// <summary>The 5-bit checksum of a block address: a CRC over the two
        /// bytes of the 11-bit block number with generator 0x15 (getCrc5).</summary>
        public static byte AddressChecksum(int address)
        {
            int block = (address >> 5) & 0x7FF;
            byte crc = 0;
            crc ^= (byte)(block >> 8);
            for (int i = 0; i < 8; i++) crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ (0x15 << 3)) : (byte)(crc << 1);
            crc ^= (byte)block;
            for (int i = 0; i < 8; i++) crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ (0x15 << 3)) : (byte)(crc << 1);
            return (byte)(crc >> 3);
        }

        /// <summary>The address field a read or write carries: the block's
        /// byte address with its checksum in the low five bits.</summary>
        public static ushort AddressField(int block)
        {
            if ((uint)block >= Blocks) throw new ArgumentOutOfRangeException(nameof(block));
            int address = block * BlockBytes;
            return (ushort)(address | AddressChecksum(address));
        }

        /// <summary>The CRC-8 over a block's 32 bytes, generator 0x85
        /// (getCrc8).</summary>
        public static byte DataCrc(ReadOnlySpan<byte> data)
        {
            byte crc = 0;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++) crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x85) : (byte)(crc << 1);
            }
            return crc;
        }

        public static byte[] StatusMessage() => new[] { CommandStatus };

        public static byte[] ReadMessage(int block)
        {
            ushort field = AddressField(block);
            return new[] { CommandRead, (byte)(field >> 8), (byte)field };
        }

        public static byte[] WriteMessage(int block, ReadOnlySpan<byte> data)
        {
            if (data.Length != BlockBytes) throw new ArgumentException($"A block is {BlockBytes} bytes.", nameof(data));
            ushort field = AddressField(block);
            var message = new byte[3 + BlockBytes];
            message[0] = CommandWrite;
            message[1] = (byte)(field >> 8);
            message[2] = (byte)field;
            data.CopyTo(message.AsSpan(3));
            return message;
        }

        /// <summary>The status reply is the controller's two identity bytes
        /// and its status. The low two bits say whether a pak is in: 1
        /// present, 3 present and changed (libdragon's
        /// JOYBUS_IDENTIFY_STATUS_ACCESSORY_*), 2 or 0 absent.</summary>
        public static bool IsPakPresent(ReadOnlySpan<byte> reply)
            => reply.Length >= 3 && (reply[2] & 0x03) is 0x01 or 0x03;

        public enum BlockResult { Ok, BadCrc, NoPak, Short }

        /// <summary>Checks a read reply, 32 data bytes and their CRC, and
        /// copies the data out when it is good.</summary>
        public static BlockResult ParseRead(ReadOnlySpan<byte> reply, Span<byte> data)
        {
            if (reply.Length < BlockBytes + 1) return BlockResult.Short;
            var block = reply.Slice(0, BlockBytes);
            byte expected = DataCrc(block);
            byte crc = reply[BlockBytes];
            if (crc == expected)
            {
                block.CopyTo(data);
                return BlockResult.Ok;
            }
            return crc == (byte)~expected ? BlockResult.NoPak : BlockResult.BadCrc;
        }

        /// <summary>Checks a write reply, the CRC the pak computed over what
        /// it stored.</summary>
        public static BlockResult ParseWrite(ReadOnlySpan<byte> reply, ReadOnlySpan<byte> data)
        {
            if (reply.Length < 1) return BlockResult.Short;
            byte expected = DataCrc(data);
            if (reply[0] == expected) return BlockResult.Ok;
            return reply[0] == (byte)~expected ? BlockResult.NoPak : BlockResult.BadCrc;
        }

        /// <summary>True for block 0 as a Rumble Pak answers it.</summary>
        public static bool IsRumblePak(ReadOnlySpan<byte> reply)
            => reply.Length >= BlockBytes + 1 && reply[0] == RumblePakFill && reply[BlockBytes] == RumblePakCrc;
    }
}
