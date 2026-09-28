using System;
using System.Collections.Generic;
using System.Globalization;

namespace PadForge.Engine.Common.AnalogKeyboard.Routes
{
    /// <summary>One model a UUID admits, from Data/addressed.json.</summary>
    public sealed class AddressedModel
    {
        public AddressedModel(ulong uuid, string name, int[] table, ushort[] order)
        {
            Uuid = uuid;
            Name = name;
            Table = table;
            Order = order;
        }

        /// <summary>The six-byte model UUID the identity read returns.</summary>
        public ulong Uuid { get; }

        /// <summary>The model's name as HallJoy's documents give it.</summary>
        public string Name { get; }

        /// <summary>The factory key table: index = the key ID (IPI) or
        /// position (HERO) the keyboard reports, value = key code, 0 where the
        /// model has no key.</summary>
        public int[] Table { get; }

        /// <summary>The IDs in the order HallJoy reads the key map: ascending
        /// for IPI, the table's source order for HERO.</summary>
        public ushort[] Order { get; }
    }

    /// <summary>
    /// The routes of HallJoy's "09" frame family (issue #468), ported from
    /// HallJoy (AGPL-3.0, commit 378f9fe): the AULA HERO boards, the IPI
    /// keyboards on the Addressed protocol, and other keyboards that answer
    /// the Addressed probe. <see cref="All"/> lists them in HallJoy's
    /// priority: the exact HERO UUID admission precedes the Addressed route
    /// (native_analog_backends.def:24-26), and inside the Addressed route the
    /// IPI identities take the exact-UUID path before anything reaches the
    /// probe (addressed_analog_backend.cpp:484-486, 692-693).
    /// </summary>
    public static class AddressedRoutes
    {
        public const string AulaHeroId = "halljoy-aula-hero";
        public const string AddressedIpiId = "halljoy-addressed-ipi";
        public const string AddressedGenericId = "halljoy-addressed-generic";

        public const string DataFile = "addressed.json";

        /// <summary>The vendor-defined collection every route here talks to
        /// (addressed_analog_backend.cpp:45-46, aula_hero84he_diagnostic_protocol.h:14-15).</summary>
        public const ushort UsagePage = 0xFF60;
        public const ushort Usage = 0x0061;

        public const ushort AulaVendorId = 0x372E;
        public const ushort AulaHeroProductId = 0x103E;
        public const ushort IpiWiredProductId = 0x105C;
        public const ushort IpiWirelessProductId = 0x106C;

        /// <summary>HallJoy's Addressed session reader sets 128 input buffers
        /// (addressed_analog_backend.cpp:1146) and its probe 64 (:687). The
        /// app sets one count per open, so the session's larger one serves
        /// both. The HERO session sets 256 (aula_hero84he_backend.cpp:254).</summary>
        public const int AddressedInputBuffers = 128;
        public const int AulaHeroInputBuffers = 256;

        /// <summary>Every route of the group, in HallJoy's priority order.</summary>
        public static IReadOnlyList<AnalogKeyboardRoute> All { get; } = new[]
        {
            new AnalogKeyboardRoute
            {
                Id = AulaHeroId,
                Protocol = AnalogKeyboardProtocol.AulaHero,
                Matches = MatchesAulaHero,
                CreateSession = _ => new AulaHeroSession(),
                InputBuffers = AulaHeroInputBuffers,
                // A write waits 50 ms (kSliceMs, aula_hero84he_backend.cpp:124-155)
                // and a key expires 750 ms after its last sample (kFreshMs,
                // :39). HallJoy proves the identity once, when routing is
                // prepared (:501-522), and its worker reopens only the claimed
                // collection, every second (:474-497).
                WriteTimeoutMs = 50,
                StaleAfterMs = 750,
                ProbeOnce = true,
                StartRetryMs = 1000,
                ReconnectMs = 1000,
            },
            new AnalogKeyboardRoute
            {
                Id = AddressedIpiId,
                Protocol = AnalogKeyboardProtocol.AddressedIpi,
                Matches = MatchesAddressedIpi,
                CreateSession = _ => new AddressedIpiSession(),
                InputBuffers = AddressedInputBuffers,
                // A key expires 500 ms after its last answer (kFreshMs,
                // addressed_analog_backend.cpp:57). After routing is prepared
                // HallJoy reopens only a collection it proved before
                // (TryClaimCandidate, :745-756): 500 ms after a session, and
                // every 5 s while the claim fails (:1510-1533).
                StaleAfterMs = 500,
                ProbeOnce = true,
                StartRetryMs = 5000,
                ReconnectMs = 500,
            },
            new AnalogKeyboardRoute
            {
                Id = AddressedGenericId,
                Protocol = AnalogKeyboardProtocol.AddressedGeneric,
                Matches = MatchesAddressedGeneric,
                CreateSession = info => new AddressedGenericSession(info.VendorId),
                InputBuffers = AddressedInputBuffers,
                StaleAfterMs = 500,
                ProbeOnce = true,
                StartRetryMs = 5000,
                ReconnectMs = 500,
            },
        };

        // ── Identification ──

        /// <summary>
        /// The AULA HERO family's collection, HallJoy's exact filter
        /// (aula_hero84he_backend.cpp:176-233): 372E:103E, usage FF60:0061,
        /// input and output reports of exactly 64 bytes, and report ID 9 among
        /// both the input and the output reports. Every HERO model shares this
        /// identity, and the UUID the session reads picks the model.
        /// </summary>
        public static bool MatchesAulaHero(AnalogKeyboardDeviceInfo info)
            => info != null
               && info.VendorId == AulaVendorId && info.ProductId == AulaHeroProductId
               && info.UsagePage == UsagePage && info.Usage == Usage
               && info.InputReportLength == AddressedFrame.Length
               && info.OutputReportLength == AddressedFrame.Length
               && DeclaresReport9(info);

        /// <summary>
        /// An IPI keyboard: 372E:105C (wired) or 372E:106C (wireless) with the
        /// Addressed fingerprint (addressed_analog_backend.cpp:304-309,
        /// 484-486). HallJoy checks no report ID here and sizes its buffers
        /// from the caps, and so does this route. The UUID read in the session
        /// decides the model. HallJoy's IPI gate leaves out the IPI "Plus"
        /// revisions on 372E:10E0, which reach its probe instead, as they
        /// reach the generic route here.
        /// </summary>
        public static bool MatchesAddressedIpi(AnalogKeyboardDeviceInfo info)
            => info != null
               && info.VendorId == AulaVendorId
               && (info.ProductId == IpiWiredProductId || info.ProductId == IpiWirelessProductId)
               && HasAddressedFingerprint(info);

        /// <summary>
        /// Any other keyboard that may speak the Addressed protocol, the
        /// collections HallJoy offers its capability probe.
        ///
        /// <para>Deviation from HallJoy, for safety: HallJoy probes every
        /// FF60:0061 collection whose reports are at least 64 bytes, whatever
        /// the vendor (addressed_analog_backend.cpp:304-309, 704-737). That is
        /// also the QMK/VIA raw HID interface, which declares no report ID and
        /// 33-byte reports, and where VIA command 0x09 saves settings to
        /// EEPROM. This route admits a collection only when it declares report
        /// ID 9 for both input and output with 64-byte reports, the shape the
        /// 09 frame needs, and never a keyboard another route owns on this
        /// usage: Keychron (3434), Lemokey (362D) and Madlions (373B), the
        /// IPI identities 372E:105C and 372E:106C, and the HERO identity
        /// 372E:103E. HallJoy itself keeps the IPI identities out of its probe
        /// (addressed_analog_backend.cpp:692-693) but would let an unclaimed
        /// HERO board reach it.</para>
        /// </summary>
        public static bool MatchesAddressedGeneric(AnalogKeyboardDeviceInfo info)
            => info != null
               && HasAddressedFingerprint(info)
               && info.InputReportLength == AddressedFrame.Length
               && info.OutputReportLength == AddressedFrame.Length
               && DeclaresReport9(info)
               && !OwnedByAnotherRoute(info.VendorId, info.ProductId);

        /// <summary>HallJoy's Addressed fingerprint: usage FF60:0061 with input
        /// and output reports of at least 64 bytes
        /// (IsFingerprintCandidate, addressed_analog_backend.cpp:304-309).</summary>
        public static bool HasAddressedFingerprint(AnalogKeyboardDeviceInfo info)
            => info.UsagePage == UsagePage && info.Usage == Usage
               && info.InputReportLength >= AddressedFrame.Length
               && info.OutputReportLength >= AddressedFrame.Length;

        /// <summary>The identities the generic route leaves to their own
        /// routes.</summary>
        public static bool OwnedByAnotherRoute(ushort vendorId, ushort productId)
        {
            switch (vendorId)
            {
                case AnalogKeyboardCatalog.KeychronVendorId:
                case AnalogKeyboardCatalog.LemokeyVendorId:
                case AnalogKeyboardCatalog.MadlionsVendorId:
                    return true;
                case AulaVendorId:
                    return productId is AulaHeroProductId or IpiWiredProductId or IpiWirelessProductId;
            }
            return false;
        }

        private static bool DeclaresReport9(AnalogKeyboardDeviceInfo info)
            => info.HasInputReport?.Invoke(AddressedFrame.ReportId) == true
               && info.HasOutputReport?.Invoke(AddressedFrame.ReportId) == true;

        // ── Model catalogs (Data/addressed.json) ──

        private static readonly Lazy<AddressedModel[]> _ipiModels = new(() => LoadModels("ipi_models", false));
        private static readonly Lazy<AddressedModel[]> _heroModels = new(() => LoadModels("aula_hero_models", true));

        /// <summary>
        /// The eight IPI models HallJoy admits by UUID (generated/ipi_models.h:16-25),
        /// each with the factory table its key IDs come from. HallJoy binds no
        /// UUID to a PID: any of them is accepted on 105C or 106C
        /// (addressed_analog_backend.cpp:484-486, 506). The names are the
        /// per-UUID names of HallJoy's SUPPORTED_HARDWARE.md, matched to the
        /// UUIDs through the vendor catalog HallJoy cached
        /// (docs/research/remaining-layout-sources-20260914/ipi-devices.json).
        /// </summary>
        public static IReadOnlyList<AddressedModel> IpiModels => _ipiModels.Value;

        /// <summary>The seven AULA HERO UUIDs HallJoy admits
        /// (aula_hero_family.h:178-188), named as HallJoy's support notices
        /// name them (docs/development/keyboard_support_notices.json:150-185).</summary>
        public static IReadOnlyList<AddressedModel> AulaHeroModels => _heroModels.Value;

        /// <summary>The IPI model for a UUID, or null (ipi::FindModel).</summary>
        public static AddressedModel FindIpiModel(ulong uuid) => Find(IpiModels, uuid);

        /// <summary>The HERO model for a UUID, or null (hero_family::Find).</summary>
        public static AddressedModel FindAulaHeroModel(ulong uuid) => Find(AulaHeroModels, uuid);

        /// <summary>The generic route's fallback table, HallJoy's
        /// kCanonicalKeys (addressed_analog_backend.cpp:67-89): index = key
        /// ID, value = key code, 0 for the three IDs it leaves unassigned.</summary>
        public static int[] CanonicalTable => AnalogKeyboardData.Table(DataFile, "addressed_canonical");

        /// <summary>kCanonicalKeys' key IDs in source order, the profile order
        /// of the fallback.</summary>
        public static int[] CanonicalOrder => AnalogKeyboardData.Table(DataFile, "addressed_canonical_order");

        private static AddressedModel Find(IReadOnlyList<AddressedModel> models, ulong uuid)
        {
            foreach (var model in models)
                if (model.Uuid == uuid) return model;
            return null;
        }

        private static AddressedModel[] LoadModels(string property, bool sourceOrder)
        {
            var root = AnalogKeyboardData.File(DataFile);
            var list = new List<AddressedModel>();
            foreach (var entry in root.GetProperty(property).EnumerateArray())
            {
                ulong uuid = ulong.Parse(entry.GetProperty("uuid").GetString(), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture);
                string name = entry.GetProperty("name").GetString();
                string tableName = entry.GetProperty("table").GetString();
                int[] table = AnalogKeyboardData.Table(DataFile, tableName);
                ushort[] order;
                if (sourceOrder)
                {
                    int[] source = AnalogKeyboardData.Table(DataFile, tableName + "_order");
                    order = Array.ConvertAll(source, p => (ushort)p);
                }
                else
                {
                    // Every IPI catalog ID has a nonzero factory code, so the
                    // table's nonzero indices ascending are HallJoy's request
                    // order (generated/ipi_models.h:8-15).
                    var ids = new List<ushort>();
                    for (int i = 1; i < table.Length; i++)
                        if (table[i] != 0) ids.Add((ushort)i);
                    order = ids.ToArray();
                }
                list.Add(new AddressedModel(uuid, name, table, order));
            }
            return list.ToArray();
        }
    }
}
