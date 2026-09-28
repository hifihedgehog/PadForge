using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PadForge.Engine.Common.AnalogKeyboard.Routes
{
    /// <summary>
    /// HallJoy's routes over the 5C frame on vendor usage page FFA0, usage 1
    /// (issue #468), in HallJoy's catalog order (native_analog_backends.def:27,
    /// 41-42): the SparkPlayJoy RM 6x21 route (AULA and GravaStar boards),
    /// the JingTai V1 route (IROK MG75 Pro and the pinned-map JingTai V1
    /// models) and the Chilkey Slice75 HE route. Every one opens the
    /// collection exclusively with 64 input buffers, because no answer
    /// carries a transaction ID (aula_win60he_backend.cpp:1180-1187,
    /// mg75_pro_backend.cpp:221-234, slice75_backend.cpp:213-222).
    ///
    /// <para>Identities, model tables and names come from jingtai.json,
    /// generated from HallJoy's headers: mg75_pro_protocol.h,
    /// jingtai_v1_profiles.h, slice75_protocol.h and aula_win60he_protocol.h.</para>
    /// </summary>
    public static class JingTaiRoutes
    {
        public const string DataFile = "jingtai.json";

        // First, so it exists before any route below is built. It loads on
        // first use, on the sweep's worker.
        private static readonly Lazy<CatalogData> _catalog = new(LoadCatalog);

        /// <summary>SparkPlayJoy RM 6x21, HallJoy's "aula-sparkplayjoy-6x21"
        /// descriptor (aula_win60he_backend.cpp:2773-2795).</summary>
        public static readonly AnalogKeyboardRoute AulaRm = new()
        {
            Id = "halljoy-aula-sparkplayjoy-6x21",
            Protocol = AnalogKeyboardProtocol.AulaRm,
            Matches = MatchesAulaRm,
            CreateSession = info => new AulaRmSession(info.VendorId, info.ProductId, Product(info)),
            Writable = true,
            Exclusive = true,
            InputBuffers = 64,
            // A proof that failed on a transfer or a decode is tried again
            // after 100 ms, and so is a session that ended
            // (WaitForReconnect(100), aula_win60he_backend.cpp:1960-1963,
            // 2300-2306). A proof refused on its content waits for the device
            // to change (IsDeterministicSemanticFailure, :1819-1834), which
            // AulaRmSession marks with NoStartRetry.
            StartRetryMs = 100,
            ReconnectMs = 100,
            Name = AulaRmName,
        };

        /// <summary>JingTai V1, HallJoy's "irok-mg75-pro" descriptor
        /// (mg75_pro_backend.cpp:567-587).</summary>
        public static readonly AnalogKeyboardRoute JingTaiV1 = new()
        {
            Id = "halljoy-irok-mg75-pro",
            Protocol = AnalogKeyboardProtocol.JingTaiV1,
            Matches = MatchesJingTaiV1,
            CreateSession = info =>
            {
                var model = FindJingTaiModel(info.VendorId, info.ProductId, Product(info));
                return model == null ? null : new JingTaiSession(model);
            },
            Writable = true,
            Exclusive = true,
            InputBuffers = 64,
            // Writes wait 50 ms (mg75_pro_backend.cpp:235-260), a value reads
            // 0 150 ms after its read (:34), and the worker runs admission
            // again every second whether or not a session ran (:425-440).
            WriteTimeoutMs = 50,
            StaleAfterMs = 150,
            StartRetryMs = 1000,
            ReconnectMs = 1000,
            Name = info => FindJingTaiModel(info.VendorId, info.ProductId, Product(info))?.Name,
            Keys = info =>
            {
                var model = FindJingTaiModel(info.VendorId, info.ProductId, Product(info));
                return model == null ? null : AnalogKeyboardData.KeysOf(model.Table);
            },
        };

        /// <summary>Chilkey Slice75 HE, HallJoy's "chilkey-slice75" descriptor
        /// (slice75_backend.cpp:548-568).</summary>
        public static readonly AnalogKeyboardRoute ChilkeySlice75 = new()
        {
            Id = "halljoy-chilkey-slice75",
            Protocol = AnalogKeyboardProtocol.ChilkeySlice75,
            Matches = MatchesSlice75,
            CreateSession = _ => new JingTaiSession(Slice75Model),
            Writable = true,
            Exclusive = true,
            InputBuffers = 64,
            // The same budgets (slice75_backend.cpp:30, 232, 421).
            WriteTimeoutMs = 50,
            StaleAfterMs = 150,
            StartRetryMs = 1000,
            ReconnectMs = 1000,
            Name = _ => Slice75Model.Name,
            Keys = _ => AnalogKeyboardData.KeysOf(Slice75Model.Table),
        };

        /// <summary>The routes in HallJoy's priority order: AulaWin60He
        /// precedes Mg75Pro, which precedes Slice75.</summary>
        public static IReadOnlyList<AnalogKeyboardRoute> All { get; } = new[] { AulaRm, JingTaiV1, ChilkeySlice75 };

        // ── Identification ──────────────────────────────────────────────────

        /// <summary>
        /// The RM 6x21 collections HallJoy would examine and fingerprint
        /// (EnumerateCandidates and IsAulaFamilyIdentity,
        /// aula_win60he_backend.cpp:694-708, 1016-1091). Discovery looks further
        /// only at a path holding a known VID and PID or "vid_1ca2"
        /// (:1030-1036), or a family token in the SetupAPI manufacturer,
        /// friendly name or description (IsAulaFamilySetupIdentity,
        /// :772-779). The fingerprint then needs brand
        /// evidence, usage page FFA0, usage 1 and 65-byte input and output
        /// reports. HallJoy's refusal to open anything while two family
        /// keyboards are attached is its single-session policy
        /// (aula_win60he_session_policy.cpp:146-150) and is not kept: each
        /// keyboard gets its own session and its own full proof.
        /// </summary>
        public static bool MatchesAulaRm(AnalogKeyboardDeviceInfo info)
        {
            if (info == null || info.UsagePage != AulaRmProtocol.UsagePage || info.Usage != AulaRmProtocol.Usage)
                return false;
            if (!AulaRmProtocol.PathContainsKnownUsbIdentity(info.Path) && !AulaRmProtocol.PathContainsAulaVendor(info.Path)
                && !AulaRmProtocol.ContainsFamilyToken(info.SetupManufacturer)
                && !AulaRmProtocol.ContainsFamilyToken(info.SetupFriendlyName)
                && !AulaRmProtocol.ContainsFamilyToken(info.SetupDescription))
                return false;
            return AulaRmProtocol.IsFamilyIdentity(info.VendorId, info.ProductId, info.ManufacturerString,
                Product(info), info.UsagePage, info.Usage, info.InputReportLength, info.OutputReportLength);
        }

        /// <summary>The name a board carries before its handshake: the known
        /// table's, the WIN 60 HE PRO by its exact product string
        /// (aula_win60he_backend.cpp:1570-1571), null for a sibling.</summary>
        public static string AulaRmName(AnalogKeyboardDeviceInfo info)
        {
            if (info == null) return null;
            var board = FindAulaBoard(info.VendorId, info.ProductId);
            if (board == null) return null;
            return info.VendorId == 0x1CA2 && info.ProductId == 0x1902
                && string.Equals(Product(info), AulaRmProtocol.Win60ProProduct, StringComparison.Ordinal)
                ? AulaRmProtocol.Win60ProName
                : board.Name;
        }

        /// <summary>The JingTai V1 collection shape (mg75_pro_backend.cpp:173-188,
        /// slice75_backend.cpp:171-182): usage page FFA0, usage 1, 65-byte
        /// input and output reports, and input and output reports without a
        /// report ID.</summary>
        public static bool IsJingTaiShape(AnalogKeyboardDeviceInfo info)
            => info != null && info.UsagePage == 0xFFA0 && info.Usage == 0x0001
               && info.InputReportLength == JingTaiFrames.ReportBytes
               && info.OutputReportLength == JingTaiFrames.ReportBytes
               && info.HasInputReport != null && info.HasInputReport(0)
               && info.HasOutputReport != null && info.HasOutputReport(0);

        /// <summary>The shape plus an exact identity: the VID, PID and product
        /// string of one of the 29 rows (mg75_pro_backend.cpp:195-207,
        /// mg75_pro_protocol.h:33-36, jingtai_v1_profiles.h:14-45).</summary>
        public static bool MatchesJingTaiV1(AnalogKeyboardDeviceInfo info)
            => IsJingTaiShape(info) && FindJingTaiModel(info.VendorId, info.ProductId, Product(info)) != null;

        /// <summary>The shape plus 1CA3:0701 and the product string
        /// "SLICE75 HE" (slice75_backend.cpp:164-198, slice75_protocol.h:34-37).</summary>
        public static bool MatchesSlice75(AnalogKeyboardDeviceInfo info)
            => IsJingTaiShape(info) && info.VendorId == Slice75VendorId && info.ProductId == Slice75ProductId
               && NormalizeProduct(Product(info)) == Slice75Product;

        /// <summary>The product string as the device returns it, which HallJoy
        /// compares after trimming its end only. Collections described by hand
        /// (tests) carry only the trimmed string.</summary>
        public static string Product(AnalogKeyboardDeviceInfo info)
            => string.IsNullOrEmpty(info.RawProductString) ? info.ProductString : info.RawProductString;

        /// <summary>A product string the way HallJoy compares it: trailing
        /// white space removed and upper-cased with towupper, which in the C
        /// locale HallJoy runs in changes ASCII letters only
        /// (mg75_pro_backend.cpp:195-203).</summary>
        public static string NormalizeProduct(string product)
        {
            if (string.IsNullOrEmpty(product)) return string.Empty;
            var chars = product.TrimEnd().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] >= 'a' && chars[i] <= 'z') chars[i] = (char)(chars[i] - 'a' + 'A');
            return new string(chars);
        }

        /// <summary>The model an exact (VID, PID, product) tuple names, or
        /// null (mg75pro::ExactModel then jt::Find).</summary>
        public static JingTaiModel FindJingTaiModel(ushort vendorId, ushort productId, string product)
        {
            string key = NormalizeProduct(product);
            if (key.Length == 0) return null;
            foreach (var identity in Catalog.Identities)
                if (identity.VendorId == vendorId && identity.ProductId == productId && identity.Product == key)
                    return identity.Model;
            return null;
        }

        /// <summary>The known RM 6x21 board for a VID and PID, or null
        /// (FindKnownUsbIdentity, aula_win60he_protocol.h:38-46).</summary>
        public static AulaRmBoard FindAulaBoard(ushort vendorId, ushort productId)
        {
            foreach (var board in Catalog.Boards)
                if (board.VendorId == vendorId && board.ProductId == productId) return board;
            return null;
        }

        // ── Catalog ─────────────────────────────────────────────────────────

        public const ushort Slice75VendorId = 0x1CA3;
        public const ushort Slice75ProductId = 0x0701;
        public const string Slice75Product = "SLICE75 HE";

        /// <summary>The Chilkey Slice75 HE (slice75_protocol.h:10-37): the
        /// firmware 1.1.7.3 factory matrix, 80 keys, a 3300 um range, and the
        /// factory-row proof.</summary>
        public static JingTaiModel Slice75Model => Catalog.Slice75;

        /// <summary>Every JingTai V1 model, the MG75 Pro first, then HallJoy's
        /// declaration order (jingtai_v1_profiles.h:6-12).</summary>
        public static IReadOnlyList<JingTaiModel> JingTaiModels => Catalog.Models;

        /// <summary>The 29 admitted (VID, PID, product) tuples.</summary>
        public static IReadOnlyList<JingTaiIdentity> JingTaiIdentities => Catalog.Identities;

        /// <summary>HallJoy's six known RM 6x21 boards.</summary>
        public static IReadOnlyList<AulaRmBoard> AulaBoards => Catalog.Boards;

        private static CatalogData Catalog => _catalog.Value;

        private sealed class CatalogData
        {
            public List<JingTaiModel> Models = new();
            public List<JingTaiIdentity> Identities = new();
            public List<AulaRmBoard> Boards = new();
            public JingTaiModel Slice75;
        }

        private static CatalogData LoadCatalog()
        {
            var data = new CatalogData();
            JsonElement root = AnalogKeyboardData.File(DataFile);
            var byIdentity = new Dictionary<string, JingTaiModel>(StringComparer.Ordinal);
            foreach (var m in root.GetProperty("jingtai_models").EnumerateArray())
            {
                var model = new JingTaiModel
                {
                    Identity = m.GetProperty("identity").GetString(),
                    Name = m.GetProperty("name").GetString(),
                    Table = AnalogKeyboardData.Table(DataFile, m.GetProperty("table").GetString()),
                    Count = m.GetProperty("count").GetInt32(),
                    Range = m.GetProperty("range").GetInt32(),
                    FactoryProof = m.GetProperty("factoryProof").GetBoolean(),
                };
                data.Models.Add(model);
                byIdentity[model.Identity] = model;
            }
            foreach (var i in root.GetProperty("jingtai_identities").EnumerateArray())
            {
                data.Identities.Add(new JingTaiIdentity
                {
                    VendorId = (ushort)i.GetProperty("vid").GetInt32(),
                    ProductId = (ushort)i.GetProperty("pid").GetInt32(),
                    Product = i.GetProperty("product").GetString(),
                    Model = byIdentity[i.GetProperty("model").GetString()],
                });
            }
            var s = root.GetProperty("slice75_model");
            data.Slice75 = new JingTaiModel
            {
                Identity = s.GetProperty("identity").GetString(),
                Name = s.GetProperty("name").GetString(),
                Table = AnalogKeyboardData.Table(DataFile, s.GetProperty("table").GetString()),
                Count = s.GetProperty("count").GetInt32(),
                Range = s.GetProperty("range").GetInt32(),
                FactoryProof = s.GetProperty("factoryProof").GetBoolean(),
            };
            foreach (var b in root.GetProperty("aula_rm_boards").EnumerateArray())
            {
                data.Boards.Add(new AulaRmBoard
                {
                    VendorId = (ushort)b.GetProperty("vid").GetInt32(),
                    ProductId = (ushort)b.GetProperty("pid").GetInt32(),
                    BoardId = (uint)b.GetProperty("board").GetInt64(),
                    Name = b.GetProperty("name").GetString(),
                });
            }
            return data;
        }
    }

    /// <summary>One admitted JingTai V1 identity (jingtai_v1_profiles.h:13):
    /// the product string as HallJoy compares it, upper case.</summary>
    public sealed class JingTaiIdentity
    {
        public ushort VendorId { get; init; }
        public ushort ProductId { get; init; }
        public string Product { get; init; } = string.Empty;
        public JingTaiModel Model { get; init; }
    }
}
