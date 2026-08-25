using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Reads the firmware's ACPI-WMI registrations (issue #343 follow-up).
    /// Every vendor hotkey that reaches Windows as a WMI event does so
    /// through the ACPI-WMI mapper (the <c>ACPI\PNP0C14</c> device): the
    /// firmware's <c>_WDG</c> object lists each GUID it serves, and an
    /// entry with the event flag names an event GUID. Restricting the
    /// learner's subscriptions to these GUIDs is what keeps it away from
    /// every other WMI provider on the machine (audio, network, storage
    /// miniports behind Microsoft class drivers), one of which
    /// double-completed an enable request and bug-checked the bench
    /// machine (0x44, WmipSendWmiIrp, 2026-08-25). No vendor names: the
    /// table is the firmware's own declaration, whoever wrote it.
    ///
    /// <para>Layout per Linux drivers/platform/x86/wmi.c (struct
    /// guid_block, 20 bytes): 16-byte GUID, then either a two-character
    /// object id or a notify id plus a reserved byte, then instance_count,
    /// then flags, where ACPI_WMI_EVENT is 0x08. The AML carries it as
    /// Name(_WDG, Buffer(N) {...}).</para>
    /// </summary>
    public static class AcpiWmi
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint EnumSystemFirmwareTables(uint provider, byte[] buffer, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[] buffer, uint size);

        private const uint ProviderAcpi = 0x41435049; // 'ACPI'
        public const byte FlagExpensive = 0x01;
        public const byte FlagMethod = 0x02;
        public const byte FlagString = 0x04;
        public const byte FlagEvent = 0x08;

        public readonly struct Block
        {
            public readonly Guid Guid;
            public readonly byte Flags;
            public readonly byte NotifyId;
            public readonly byte InstanceCount;
            public Block(Guid guid, byte flags, byte notifyId, byte instanceCount)
            { Guid = guid; Flags = flags; NotifyId = notifyId; InstanceCount = instanceCount; }
            public bool IsEvent => (Flags & FlagEvent) != 0;
        }

        /// <summary>Every <c>_WDG</c> entry in the DSDT and every SSDT. Empty
        /// when the tables cannot be read. Blocking firmware reads: worker
        /// thread only.</summary>
        public static List<Block> ReadBlocks()
        {
            var blocks = new List<Block>();
            try
            {
                foreach (var table in ReadAcpiTables())
                    ParseWdg(table, blocks);
            }
            catch { }
            return blocks;
        }

        /// <summary>The event GUIDs the firmware declares.</summary>
        public static HashSet<Guid> ReadEventGuids()
        {
            var set = new HashSet<Guid>();
            foreach (var b in ReadBlocks())
                if (b.IsEvent) set.Add(b.Guid);
            return set;
        }

        private static IEnumerable<byte[]> ReadAcpiTables()
        {
            var result = new List<byte[]>();
            uint need = EnumSystemFirmwareTables(ProviderAcpi, null, 0);
            if (need == 0 || need > 1 << 16) return result;
            var ids = new byte[need];
            if (EnumSystemFirmwareTables(ProviderAcpi, ids, need) != need) return result;
            // DSDT is not listed by the enumerator (it hangs off the FADT);
            // ask for it by name alongside every SSDT.
            var wanted = new List<uint> { Sig("DSDT") };
            for (int i = 0; i + 4 <= ids.Length; i += 4)
            {
                uint id = BitConverter.ToUInt32(ids, i);
                if (id == Sig("SSDT")) wanted.Add(id);
            }
            // GetSystemFirmwareTable returns the FIRST table with a given
            // signature. SSDTs share one, so the enumerator's duplicates
            // collapse; the mapper's _WDG almost always sits in the DSDT or
            // the first SSDT carrying it, and both are read here.
            var seen = new HashSet<uint>();
            foreach (uint id in wanted)
            {
                if (!seen.Add(id)) continue;
                uint size = GetSystemFirmwareTable(ProviderAcpi, id, null, 0);
                if (size == 0 || size > 1 << 24) continue;
                var buf = new byte[size];
                if (GetSystemFirmwareTable(ProviderAcpi, id, buf, size) != size) continue;
                result.Add(buf);
            }
            return result;
        }

        private static uint Sig(string s) =>
            (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));

        /// <summary>Scans AML bytes for Name(_WDG, Buffer(...)) objects and
        /// appends their entries. Pure, so a synthetic table tests it.</summary>
        public static void ParseWdg(byte[] aml, List<Block> into)
        {
            if (aml == null || into == null) return;
            for (int i = 0; i + 6 <= aml.Length; i++)
            {
                // '_WDG' followed by BufferOp (0x11).
                if (aml[i] != (byte)'_' || aml[i + 1] != (byte)'W' || aml[i + 2] != (byte)'D' || aml[i + 3] != (byte)'G') continue;
                int p = i + 4;
                if (p >= aml.Length || aml[p] != 0x11) continue;
                p++;
                if (!ReadPkgLength(aml, ref p, out int pkgLen)) continue;
                int pkgStart = p; // PkgLength counts from its own first byte
                // BufferSize is an integer term: ZeroOp, OneOp, ByteConst,
                // WordConst, DWordConst.
                if (p >= aml.Length) continue;
                long bufferSize;
                switch (aml[p])
                {
                    case 0x00: bufferSize = 0; p++; break;
                    case 0x01: bufferSize = 1; p++; break;
                    case 0x0A: if (p + 1 >= aml.Length) continue; bufferSize = aml[p + 1]; p += 2; break;
                    case 0x0B: if (p + 2 >= aml.Length) continue; bufferSize = BitConverter.ToUInt16(aml, p + 1); p += 3; break;
                    case 0x0C: if (p + 4 >= aml.Length) continue; bufferSize = BitConverter.ToUInt32(aml, p + 1); p += 5; break;
                    default: continue;
                }
                if (bufferSize <= 0 || p + bufferSize > aml.Length) continue;
                int entries = (int)(bufferSize / 20);
                for (int e = 0; e < entries; e++)
                {
                    int o = p + e * 20;
                    var g = new byte[16];
                    Array.Copy(aml, o, g, 0, 16);
                    byte flags = aml[o + 19];
                    byte notify = aml[o + 16];
                    into.Add(new Block(new Guid(g), flags, notify, aml[o + 18]));
                }
                i = p + (int)bufferSize - 1;
            }
        }

        // AML PkgLength: bits 7..6 of the lead byte count the extra bytes
        // (0 to 3). With none, bits 5..0 are the length. With extras, bits
        // 3..0 of the lead byte are the low nibble and each extra byte adds
        // eight bits above it.
        private static bool ReadPkgLength(byte[] aml, ref int p, out int length)
        {
            length = 0;
            if (p >= aml.Length) return false;
            byte lead = aml[p];
            int extra = lead >> 6;
            if (p + extra >= aml.Length) return false;
            if (extra == 0) { length = lead & 0x3F; p += 1; return true; }
            int v = lead & 0x0F;
            for (int k = 0; k < extra; k++) v |= aml[p + 1 + k] << (4 + 8 * k);
            length = v;
            p += 1 + extra;
            return true;
        }
    }
}
