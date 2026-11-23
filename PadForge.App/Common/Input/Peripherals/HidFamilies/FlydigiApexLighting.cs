using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using PadForge.Common.Input.Flydigi;
using PadForge.Common.Input.Peripherals.HidFamilies;
using PadForge.Engine.Flydigi;
using Proto = PadForge.Engine.Flydigi.FlydigiApexProtocol;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The lights of a Flydigi Apex 4, Apex 5 and Apex 6 Pro (#586), on the
    /// pad's vendor collection, usage page 0xFFA0. A pad is one of this
    /// family's only once it has answered PadForge's identity request, this
    /// run or an earlier one (<see cref="FlydigiApexService.ModelAt"/>),
    /// since a Vader 3 and a Vader 4 share the Apex 4's IDs and the plain
    /// Apex 6 shares the Pro's.
    ///
    /// <para>The pad has one session with PadForge, which also writes its
    /// trigger effects and its rumble (<see cref="FlydigiApexSession"/>),
    /// and every exchange here goes through it, so a transfer of many
    /// packets is never cut by a trigger command. The channel the worker
    /// opens stands for the claim and carries nothing.</para>
    ///
    /// <para>Apex 5, twelve lights (ApexSenseBridge LightbarBridge.cpp and
    /// Apex5Device.cpp, openflydigi flydigi/lighting.py and blobs.py). The
    /// pad plays ten frames of twelve lights from a 380-byte block it keeps
    /// per profile slot, and a write changes its working copy, which the
    /// pad drops at its next sleep (openflydigi PROTOCOL.md section 9). A
    /// session asks the active slot twice, 50 ms apart, and takes no pad
    /// whose two answers differ or whose Switch bank is active
    /// (BridgeCommand.cpp), then reads the block. A picture is that block
    /// with every frame the same, sent latched in nineteen acknowledged
    /// packets, then its first packet again with the latch clear. The
    /// hand-back sends the block that was read the same way and applies the
    /// profile. The reference sends two pictures no closer than 80 ms
    /// apart.</para>
    ///
    /// <para>Apex 4, four groups (flydigi-space-station-mac
    /// DeviceSession.swift, LEDConfig.swift, docs/protocol.md sections 5,
    /// 10 and 12). The pad keeps a 500-byte block per slot: sixteen groups
    /// of ten units, four groups in use, the channels in percent. In DInput
    /// mode a write shows at once and is gone when the pad is switched off,
    /// since nothing here sends the save command. A picture is the block
    /// that was read with the steady mode and each group's units its color,
    /// sent as a start and fifty parcels, each acknowledged. A parcel the
    /// pad does not acknowledge within a second fails the picture, where
    /// the reference sends on. Nothing else is written to the pad while a
    /// block goes out, so a picture goes out no more than once a
    /// second.</para>
    ///
    /// <para>Apex 6 Pro, one color (ApexSenseBridge Apex6Lighting.cpp,
    /// LightbarBridge.cpp and Apex5Device.cpp readApex6Ram and
    /// writeApex6Ram). The pad keeps its lighting in RAM block 4 and stores
    /// every write, which was measured: the block is still there after the
    /// pad is switched off. So a color goes out once it has held for 1.5 s,
    /// a color within 12 a channel of the one the pad holds is not written,
    /// and no more than 300 go out in one run of PadForge, the reference's
    /// three bounds. A session reads the block, takes no pad whose block it
    /// cannot parse, and saves it in the settings file before its first
    /// write (<see cref="KeyboardProfileBackups"/>). The hand-back writes
    /// it back and reads it back, three tries. A run that ends without one
    /// leaves the copy, and the next run writes it back if the pad still
    /// shows the color PadForge left.</para>
    ///
    /// <para>Flydigi Space Station drives these pads' lights while its
    /// service runs, so the pad is left to it then
    /// (<see cref="VendorPrograms.FlydigiSpaceStation"/>).</para>
    /// </summary>
    internal sealed class FlydigiApexLighting : IHidLightingFamily
    {
        public static readonly FlydigiApexLighting Instance = new();

        /// <summary>The shortest time between two Apex 5 pictures
        /// (LightbarBridge.cpp workerLoop, kMinInterval).</summary>
        internal const int Apex5RepaintMs = 80;

        /// <summary>How long all nineteen packets of an Apex 5 read may
        /// take (Apex5Device.cpp readRgbConfig).</summary>
        internal const int Apex5ReadMs = 1500;

        /// <summary>How long the Apex 5's two slot reads are apart
        /// (BridgeCommand.cpp).</summary>
        internal const int Apex5SlotGapMs = 50;

        internal const int Apex4RepaintMs = 1000;

        /// <summary>How long all fifty parcels of an Apex 4 read may take,
        /// and one parcel's acknowledgement (DeviceSession.swift readBlob
        /// and writeBlob).</summary>
        internal const int Apex4ReadMs = 4000;
        internal const int Apex4AckMs = 1000;

        /// <summary>How long after an Apex 4 read the pad may still send
        /// the last parcel again (docs/protocol.md section 10).</summary>
        internal const int Apex4TrailingMs = 30;

        /// <summary>kApex6MinInterval, kApex6MaximumWrites and
        /// kApex6ColorTolerance (LightbarBridge.cpp).</summary>
        internal const int Apex6HoldMs = 1500;
        internal const int Apex6MaximumWrites = 300;
        internal const int Apex6ColorTolerance = 12;

        /// <summary>How long a failed Apex 6 write or restore waits before
        /// its next try, and how many tries a restore gets
        /// (writeApex6Color, restoreApex6).</summary>
        internal const int Apex6RetryMs = 300;
        internal const int Apex6RestoreTries = 3;

        /// <summary>How long a picture held back by the run's bound waits
        /// before it asks again.</summary>
        internal const int Apex6SpentMs = 60_000;

        internal const int SavingMs = SinowealthLighting.SavingMs;
        internal const int RepairTries = SinowealthLighting.RepairTries;

        public string Name => "Flydigi Apex";

        private static readonly IReadOnlyList<VendorProgram> s_programs = new[] { VendorPrograms.FlydigiSpaceStation };

        private static readonly Lazy<HidLightingModel> s_apex4 = new(() => new HidLightingModel
        {
            Family = Instance,
            Name = "Flydigi Apex 4",
            Map = new LightMap(Zones(Proto.Apex4LitGroups))
            {
                Product = Product(FlydigiApexModel.Apex4),
            },
            RepaintMs = Apex4RepaintMs,
            Programs = s_programs,
            Begin = Apex4Session.Begin,
        });

        private static readonly Lazy<HidLightingModel> s_apex5 = new(() => new HidLightingModel
        {
            Family = Instance,
            Name = "Flydigi Apex 5",
            Map = new LightMap(Zones(Proto.Apex5Leds))
            {
                Product = Product(FlydigiApexModel.Apex5),
            },
            RepaintMs = Apex5RepaintMs,
            Programs = s_programs,
            Begin = Apex5Session.Begin,
        });

        private static readonly Lazy<HidLightingModel> s_apex6 = new(() =>
        {
            string product = Product(FlydigiApexModel.Apex6Pro);
            return new HidLightingModel
            {
                Family = Instance,
                Name = "Flydigi Apex 6 Pro",
                Map = new LightMap(Zones(1)) { Product = product },
                HoldMs = Apex6HoldMs,
                RepaintMs = Apex6HoldMs,
                Programs = s_programs,
                Begin = (io, wait) => Apex6Session.Begin(io, wait, KeyboardProfileBackups.SessionKey(product, io?.Path)),
                Owed = path => KeyboardProfileBackups.Get(KeyboardProfileBackups.DeviceKey(product, path)) != null
                               && KeyboardProfileBackups.Failures(KeyboardProfileBackups.DeviceKey(product, path)) < RepairTries,
                Repair = (io, wait) => Apex6Session.Repair(io, wait, KeyboardProfileBackups.DeviceKey(product, io?.Path)),
            };
        });

        internal static string Product(FlydigiApexModel model) => model switch
        {
            FlydigiApexModel.Apex4 => $"flydigi:{Proto.Apex4VendorId:X4}:{Proto.Apex4ProductId:X4}",
            FlydigiApexModel.Apex5 => $"flydigi:{Proto.NewVendorId:X4}:{Proto.Apex5ProductId:X4}",
            FlydigiApexModel.Apex6Pro => $"flydigi:{Proto.NewVendorId:X4}:{Proto.Apex6ProductId:X4}",
            _ => null,
        };

        internal static HidLightingModel ModelFor(FlydigiApexModel model) => model switch
        {
            FlydigiApexModel.Apex4 => s_apex4.Value,
            FlydigiApexModel.Apex5 => s_apex5.Value,
            FlydigiApexModel.Apex6Pro => s_apex6.Value,
            _ => null,
        };

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection?.Path == null || collection.UsagePage != Proto.UsagePage) return null;
            if (Proto.CandidateModel(collection.VendorId, collection.ProductId) == FlydigiApexModel.None) return null;
            return ModelFor(FlydigiApexService.ModelAt(collection.Path));
        }

        /// <summary>Zones 1 to <paramref name="count"/>, each addressed by
        /// its place in the pad's block.</summary>
        internal static IReadOnlyList<LightInfo> Zones(int count)
        {
            var lights = new LightInfo[count];
            for (int i = 0; i < count; i++)
                lights[i] = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 };
            return lights;
        }

        private static FlydigiApexSession PadAt(IHidReportChannel io, FlydigiApexModel model)
        {
            var pad = FlydigiApexService.SessionAt(io?.Path);
            return pad != null && pad.Identity.Model == model ? pad : null;
        }

        // ─────────────────────────────────────────────
        //  Apex 5
        // ─────────────────────────────────────────────

        internal sealed class Apex5Session : HidLightingSession
        {
            private readonly FlydigiApexSession _pad;
            private readonly byte _slot;
            private readonly byte[] _read;
            private readonly byte _brightness;
            private byte[] _working;
            private bool _ours;
            private byte[][] _crash = Array.Empty<byte[]>();

            private Apex5Session(FlydigiApexSession pad, byte slot, byte[] read)
            {
                _pad = pad;
                _slot = slot;
                _read = read;
                _working = read;
                // The brightness Space Station stored, 100 at the most
                // (LightbarBridge.cpp storedBrightness_).
                _brightness = Math.Min(read[6], (byte)100);
            }

            /// <summary>The slot the session lights and the block it read
            /// there.</summary>
            public byte Slot => _slot;
            public byte[] Read => _read;

            public override bool Feature => false;

            /// <summary>The one command a crash can send blind: the profile
            /// applied again. The block a hand-back writes takes twenty-two
            /// acknowledged exchanges. What PadForge wrote is the pad's
            /// working copy, which the pad drops at its next sleep
            /// whatever this command does.</summary>
            public override byte[][] CrashFrames => _crash;

            public static HidLightingSession Begin(IHidReportChannel io, Action<int> wait)
            {
                var pad = PadAt(io, FlydigiApexModel.Apex5);
                if (pad == null) return null;
                int first = RawSlot(pad);
                if (first < 0) return null;
                wait?.Invoke(Apex5SlotGapMs);
                int second = RawSlot(pad);
                // A profile that changed between the two reads, or the
                // Switch bank: the pad is left alone.
                if (second != first || first >= 4) return null;
                var read = ReadBlock(pad, (byte)first);
                return read == null ? null : new Apex5Session(pad, (byte)first, read);
            }

            private static int RawSlot(FlydigiApexSession pad)
            {
                int slot = -1;
                pad.Exclusive(p =>
                {
                    p.Spaced();
                    return p.Request(Proto.Apex5ProfileStatus(), FlydigiApexSession.ReplyMs, (report, length) =>
                    {
                        if (!Proto.IsApex5Reply(report.AsSpan(0, length), Proto.CmdProfileStatus)) return false;
                        slot = Proto.ParseApex5RawSlot(report.AsSpan(0, length));
                        return true;
                    });
                });
                return slot;
            }

            /// <summary>The slot's 380 bytes, or null when a packet is
            /// missing after 1.5 s (readRgbConfig).</summary>
            internal static byte[] ReadBlock(FlydigiApexSession pad, byte slot)
            {
                var block = new byte[Proto.Apex5RgbSize];
                var got = new bool[Proto.Apex5RgbPackets];
                int count = 0;
                var packet = new byte[Proto.Apex5RgbPacket];
                bool whole = pad.Exclusive(p =>
                {
                    p.Spaced();
                    return p.Request(Proto.Apex5ReadRgb(slot), Apex5ReadMs, (report, length) =>
                    {
                        int index = Proto.ParseApex5RgbPacket(report.AsSpan(0, length), packet);
                        if (index < 0 || index >= Proto.Apex5RgbPackets || got[index]) return false;
                        got[index] = true;
                        packet.CopyTo(block, index * Proto.Apex5RgbPacket);
                        return ++count == Proto.Apex5RgbPackets;
                    });
                });
                return whole ? block : null;
            }

            /// <summary>Packets <paramref name="first"/> on of
            /// <paramref name="block"/>, the pad acknowledging the start
            /// and each packet (writeRgbConfigRange). The index a packet
            /// carries counts from the write's first.</summary>
            internal static bool WriteRange(FlydigiApexSession pad, byte slot, int first, int count, byte[] block)
            {
                pad.Spaced();
                if (!Acked(pad, Proto.Apex5WriteRgbStart(slot, (byte)first, (byte)count), Proto.CmdWriteRgbStart)) return false;
                for (int packet = first; packet < first + count; packet++)
                {
                    var report = Proto.Apex5WriteRgbPack((byte)(packet - first),
                        block.AsSpan(packet * Proto.Apex5RgbPacket, Proto.Apex5RgbPacket));
                    if (!Acked(pad, report, Proto.CmdWriteRgbPack)) return false;
                }
                return true;
            }

            private static bool Acked(FlydigiApexSession pad, byte[] request, byte command)
                => pad.Request(request, FlydigiApexSession.ReplyMs,
                    (report, length) => Proto.IsApex5Reply(report.AsSpan(0, length), command));

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int[] rgb = CoolerMasterLighting.ByAddress(lights, colors, Proto.Apex5Leds);
                var latched = Proto.Apex5RgbBlock(_working, rgb, _brightness, latched: true);
                var shown = (byte[])latched.Clone();
                shown[2] = 0;
                _ours = true;
                Track();
                bool sent = _pad.Exclusive(pad =>
                    WriteRange(pad, _slot, 0, Proto.Apex5RgbPackets, latched) && WriteRange(pad, _slot, 0, 1, shown));
                if (!sent) return HidLightingPaint.Failed;
                _working = shown;
                return HidLightingPaint.Sent;
            }

            /// <summary>The block that was read, latched, then its first
            /// packet as it was, then the profile applied
            /// (LightbarBridge.cpp restore).</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_ours) return true;
                var latched = (byte[])_read.Clone();
                latched[2] = 1;
                bool sent = _pad.Exclusive(pad =>
                    WriteRange(pad, _slot, 0, Proto.Apex5RgbPackets, latched)
                    && WriteRange(pad, _slot, 0, 1, _read)
                    && Acked(pad, Proto.Apex5ApplyProfile(_slot), Proto.CmdApplyProfile));
                if (!sent) return false;
                _working = _read;
                _ours = false;
                Track();
                return true;
            }

            private void Track()
            {
                if (_ours == _crash.Length > 0) return;
                _crash = _ours ? new[] { Proto.Apex5ApplyProfile(_slot) } : Array.Empty<byte[]>();
            }
        }

        // ─────────────────────────────────────────────
        //  Apex 4
        // ─────────────────────────────────────────────

        internal sealed class Apex4Session : HidLightingSession
        {
            private readonly FlydigiApexSession _pad;
            private readonly byte _slot;
            private readonly byte[] _read;
            private bool _ours;

            private Apex4Session(FlydigiApexSession pad, byte slot, byte[] read)
            {
                _pad = pad;
                _slot = slot;
                _read = read;
            }

            public byte Slot => _slot;
            public byte[] Read => _read;

            public override bool Feature => false;

            /// <summary>Nothing a crash can send blind: the block is fifty
            /// acknowledged parcels. The pad shows its stored lighting
            /// again once it has been switched off.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public static HidLightingSession Begin(IHidReportChannel io, Action<int> wait)
            {
                var pad = PadAt(io, FlydigiApexModel.Apex4);
                if (pad == null) return null;
                byte slot = pad.Apex4Slot();
                var read = ReadBlock(pad, slot);
                if (read == null) return null;
                // The pad may send the last parcel again, and the next
                // exchange drops what came before it.
                wait?.Invoke(Apex4TrailingMs);
                return Proto.IsApex4Led(read) ? new Apex4Session(pad, slot, read) : null;
            }

            /// <summary>The slot's 500 bytes, or null when a parcel is
            /// missing after 4 s (readBlob).</summary>
            internal static byte[] ReadBlock(FlydigiApexSession pad, byte slot)
            {
                var block = new byte[Proto.Apex4LedSize];
                var got = new bool[Proto.Apex4LedParcels];
                int count = 0;
                var parcel = new byte[Proto.Apex4LedParcel];
                bool whole = pad.Exclusive(p =>
                {
                    p.Spaced();
                    return p.Request(Proto.Apex4ReadLed(slot), Apex4ReadMs, (report, length) =>
                    {
                        int index = Proto.ParseApex4LedParcel(report.AsSpan(0, length), parcel);
                        if (index < 0 || index >= Proto.Apex4LedParcels || got[index]) return false;
                        got[index] = true;
                        parcel.CopyTo(block, index * Proto.Apex4LedParcel);
                        return ++count == Proto.Apex4LedParcels;
                    });
                });
                return whole ? block : null;
            }

            /// <summary>The start and the fifty parcels of
            /// <paramref name="block"/>, the pad acknowledging each
            /// (writeBlob).</summary>
            internal static bool WriteBlock(FlydigiApexSession pad, byte slot, byte[] block)
            {
                pad.Spaced();
                if (!Acked(pad, Proto.Apex4WriteLedStart(slot, Proto.Apex4LedParcels))) return false;
                for (int index = 0; index < Proto.Apex4LedParcels; index++)
                {
                    var report = Proto.Apex4WriteLedParcel((byte)index,
                        block.AsSpan(index * Proto.Apex4LedParcel, Proto.Apex4LedParcel));
                    if (!Acked(pad, report)) return false;
                }
                return true;
            }

            private static bool Acked(FlydigiApexSession pad, byte[] request)
                => pad.Request(request, Apex4AckMs,
                    (report, length) => Proto.ParseApex4LedAck(report.AsSpan(0, length)) >= 0);

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                var block = (byte[])_read.Clone();
                // A pad with more groups in use than the four listed shows
                // the last color on the rest.
                Proto.Apex4SteadyLed(block, CoolerMasterLighting.ByAddress(lights, colors, Proto.Apex4LitGroups));
                _ours = true;
                return _pad.Exclusive(pad => WriteBlock(pad, _slot, block)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>The block that was read, written back.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_ours) return true;
                if (!_pad.Exclusive(pad => WriteBlock(pad, _slot, _read))) return false;
                _ours = false;
                return true;
            }
        }

        // ─────────────────────────────────────────────
        //  Apex 6 Pro
        // ─────────────────────────────────────────────

        internal sealed class Apex6Session : HidLightingSession
        {
            /// <summary>How many colors each pad was sent this run, by the
            /// path of its collection.</summary>
            private static readonly ConcurrentDictionary<string, int> s_written = new(StringComparer.OrdinalIgnoreCase);

            private readonly FlydigiApexSession _pad;
            private readonly string _key;
            private readonly byte[] _original;
            private readonly bool _vibration;
            private readonly byte _brightness;
            private bool _ours;
            private int _shown = -1;

            private Apex6Session(FlydigiApexSession pad, string key, byte[] original, bool ours, int shown)
            {
                _pad = pad;
                _key = key;
                _original = original;
                _ours = ours;
                _shown = shown;
                Proto.ParseApex6Lighting(original, out _vibration, out byte brightness);
                _brightness = Math.Min(brightness, (byte)100);
            }

            /// <summary>The pad's own lighting block: an earlier run's
            /// copy where one is owed, else what the session read.</summary>
            public byte[] Original => _original;

            public override bool Feature => false;

            /// <summary>Nothing a crash can send blind: a write is a begin,
            /// its chunks and a commit, each answered. The copy in the
            /// settings file is what the next run writes back.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            internal static void ForgetWritesForTest() => s_written.Clear();

            public static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, string key)
            {
                var pad = PadAt(io, FlydigiApexModel.Apex6Pro);
                if (pad == null) return null;
                var now = ReadRam(pad, wait);
                // A block that cannot be parsed, or one too long to keep a
                // copy of, is left untouched (LightbarBridge.cpp
                // initializeApex6).
                if (now == null || !Proto.ParseApex6Lighting(now, out _, out _)
                    || now.Length > KeyboardProfileBackups.MaxContents)
                    return null;
                if (KeyboardProfileBackups.Get(key) is { } copy)
                {
                    bool same = copy.Contents.AsSpan().SequenceEqual(now);
                    // The pad still shows the color an earlier run left, so
                    // that run's copy is the pad's own lighting. Anything
                    // else was set since, and the copy goes.
                    if (Proto.ParseApex6Lighting(copy.Contents, out _, out _)
                        && (same || (IsSolid(now) && KeyboardProfileBackups.MayBePadForges(key, now))))
                        return new Apex6Session(pad, key, copy.Contents, ours: !same, shown: same ? -1 : ColorOf(now));
                    KeyboardProfileBackups.Drop(key);
                }
                return new Apex6Session(pad, key, now, ours: false, shown: -1);
            }

            /// <summary>Whether a block is one steady color, the only kind
            /// PadForge writes.</summary>
            internal static bool IsSolid(ReadOnlySpan<byte> ram)
                => Proto.ParseApex6Lighting(ram, out _, out _, out byte mode) && mode == 1;

            /// <summary>The color of a steady block: the three bytes behind
            /// the mode, which sits sixteen bytes in.</summary>
            internal static int ColorOf(ReadOnlySpan<byte> ram)
                => ram.Length >= 20 ? (ram[17] << 16) | (ram[18] << 8) | ram[19] : -1;

            /// <summary>RAM block 4, or null when the pad refused, a chunk
            /// did not come or the CRC-32 does not hold
            /// (readApex6Ram).</summary>
            internal static byte[] ReadRam(FlydigiApexSession pad, Action<int> wait)
            {
                const byte ram = Proto.Apex6RamLighting;
                var info = pad.RamExchange(Proto.Apex6RamInfo(ram), Proto.Apex6CmdRamRead, 1, wait);
                if (info == null || !Proto.ParseApex6RamInfo(info, ram, out int length, out uint crc)) return null;
                var block = new byte[length];
                int have = 0;
                for (int index = 0; have < length; index++)
                {
                    if (index > 255) return null;
                    var reply = pad.RamExchange(Proto.Apex6RamRead(ram, (byte)index), Proto.Apex6CmdRamRead, 0, wait);
                    if (reply == null) return null;
                    var chunk = Proto.ParseApex6RamBlock(reply, ram, index);
                    if (chunk.IsEmpty) return null;
                    int take = Math.Min(chunk.Length, length - have);
                    chunk.Slice(0, take).CopyTo(block.AsSpan(have));
                    have += take;
                }
                return Proto.Crc32(block) == crc ? block : null;
            }

            /// <summary>Writes RAM block 4: the begin, the chunks and the
            /// commit, each answered, and an abort where one is refused
            /// (writeApex6Ram).</summary>
            internal static bool WriteRam(FlydigiApexSession pad, byte[] block, Action<int> wait)
            {
                if (block == null || block.Length == 0 || block.Length > Proto.Apex6RamMaximum) return false;
                const byte ram = Proto.Apex6RamLighting;
                ushort length = (ushort)block.Length;
                uint crc = Proto.Crc32(block);
                bool Abort()
                {
                    pad.Exclusive(p => p.Write(Proto.Apex6RamWriteAbort()));
                    return false;
                }
                if (!Proto.Apex6RamAccepted(pad.RamExchange(Proto.Apex6RamWriteBegin(ram, length, crc), Proto.Apex6CmdRamWrite, 0, wait)))
                    return Abort();
                for (int offset = 0; offset < block.Length; offset += Proto.Apex6RamChunk)
                {
                    int size = Math.Min(Proto.Apex6RamChunk, block.Length - offset);
                    var chunk = Proto.Apex6RamWriteChunk((ushort)offset, block.AsSpan(offset, size));
                    if (!Proto.Apex6RamAccepted(pad.RamExchange(chunk, Proto.Apex6CmdRamWrite, 1, wait))) return Abort();
                }
                var commit = pad.RamExchange(Proto.Apex6RamWriteCommit(length, crc), Proto.Apex6CmdRamWrite, 2, wait);
                return commit != null && Proto.Apex6RamCommitted(commit, length, crc) || Abort();
            }

            /// <summary>Writes the pad's own block back and reads it back,
            /// three tries 300 ms apart (restoreApex6).</summary>
            private static bool Restore(FlydigiApexSession pad, byte[] original, Action<int> wait)
            {
                for (int attempt = 0; attempt < Apex6RestoreTries; attempt++)
                {
                    if (attempt > 0) wait?.Invoke(Apex6RetryMs);
                    if (WriteRam(pad, original, wait) && ReadRam(pad, wait) is { } back
                        && back.AsSpan().SequenceEqual(original))
                        return true;
                }
                return false;
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int rgb = CoolerMasterLighting.ByAddress(lights, colors, 1)[0];
                // A color this close to the one the pad holds is not worth a
                // stored write.
                if (_shown >= 0 && Near(rgb, _shown)) return HidLightingPaint.Sent;
                string path = _pad.Path ?? string.Empty;
                if (s_written.TryGetValue(path, out int written) && written >= Apex6MaximumWrites)
                {
                    LaterMs = Apex6SpentMs;
                    return HidLightingPaint.Later;
                }
                if (KeyboardProfileBackups.Get(_key) == null)
                    KeyboardProfileBackups.Take(_key, 0, _original);
                if (!KeyboardProfileBackups.IsPersisted(_key) || !KeyboardProfileBackups.ClearShown(_key))
                {
                    LaterMs = SavingMs;
                    return HidLightingPaint.Later;
                }
                var block = Proto.Apex6SolidLighting((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, _brightness, _vibration);
                _ours = true;
                _shown = -1;
                s_written[path] = written + 1;
                if (!WriteRam(_pad, block, wait))
                {
                    wait?.Invoke(Apex6RetryMs);
                    if (!WriteRam(_pad, block, wait)) return HidLightingPaint.Failed;
                }
                KeyboardProfileBackups.NoteShown(_key, block);
                _shown = rgb;
                return HidLightingPaint.Sent;
            }

            private static bool Near(int a, int b)
                => Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)) < Apex6ColorTolerance
                && Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)) < Apex6ColorTolerance
                && Math.Abs((a & 0xFF) - (b & 0xFF)) < Apex6ColorTolerance;

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_ours)
                {
                    // Nothing of PadForge's is on the pad, so no copy is owed.
                    KeyboardProfileBackups.Drop(_key);
                    return true;
                }
                if (!Restore(_pad, _original, wait)) return false;
                KeyboardProfileBackups.Drop(_key);
                _ours = false;
                _shown = -1;
                return true;
            }

            /// <summary>Settles what an earlier run left owed, with no
            /// claim on the pad: a pad that no longer shows the color
            /// PadForge left keeps what it holds. True when nothing is owed
            /// any more.</summary>
            public static bool Repair(IHidReportChannel io, Action<int> wait, string key)
            {
                if (KeyboardProfileBackups.Get(key) is not { } copy) return true;
                var pad = PadAt(io, FlydigiApexModel.Apex6Pro);
                var now = pad == null ? null : ReadRam(pad, wait);
                if (now != null)
                {
                    bool padForges = !copy.Contents.AsSpan().SequenceEqual(now)
                        && Proto.ParseApex6Lighting(copy.Contents, out _, out _)
                        && IsSolid(now) && KeyboardProfileBackups.MayBePadForges(key, now);
                    if (!padForges || Restore(pad, copy.Contents, wait))
                    {
                        KeyboardProfileBackups.Drop(key);
                        return true;
                    }
                }
                KeyboardProfileBackups.NoteFailure(key);
                return false;
            }
        }
    }
}
