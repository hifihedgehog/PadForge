using System;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>How a family delivers its key depths (issue #468).</summary>
    public enum AnalogKeyboardProtocol
    {
        None = 0,
        /// <summary>Wooting analog interface v1, usage page 0xFF54.</summary>
        WootingV1,
        /// <summary>Wooting analog interface v2, usage page 0xFF53.</summary>
        WootingV2,
        /// <summary>Razer Huntsman V2 Analog and Mini Analog, input report 7.</summary>
        RazerHuntsmanV2,
        /// <summary>Razer Huntsman V3 Pro family, input report 11.</summary>
        RazerHuntsmanV3,
        /// <summary>Razer Tartarus Pro keypad, input report 6.</summary>
        RazerTartarusPro,
        /// <summary>NuPhy HE line, one key per report.</summary>
        NuPhy,
        /// <summary>DrunkDeer, polled with report 4.</summary>
        DrunkDeer,
        /// <summary>Keychron and Lemokey HE boards over the VIA raw HID channel.</summary>
        Keychron,
        /// <summary>Madlions HE boards over the VIA raw HID channel.</summary>
        Madlions,
        /// <summary>Bytech chips (Redragon K709 HE), polled with report 9.</summary>
        Bytech,

        // HallJoy's native routes, read from its source (AGPL-3.0).

        /// <summary>ATTACK SHARK RY5088 boards, feature-report polling.</summary>
        AttackShark,
        /// <summary>AULA MINI 60 HE, HE Pro and HE MAX, the HFD command and stream protocol.</summary>
        AulaMini60,
        /// <summary>AULA HERO family (GEEHY), report 9 selected-key polling.</summary>
        AulaHero,
        /// <summary>IPI keyboards on the Addressed 09 frame protocol, admitted by UUID.</summary>
        AddressedIpi,
        /// <summary>Other keyboards on the Addressed 09 frame protocol, admitted by a probe.</summary>
        AddressedGeneric,
        /// <summary>IROK MG75 Pro and the JingTai V1 family, 5C frames.</summary>
        JingTaiV1,
        /// <summary>Chilkey Slice75 HE, JingTai V1 framing.</summary>
        ChilkeySlice75,
        /// <summary>AULA WIN 60 HE MAX and the SparkPlayJoy RM 6x21 family.</summary>
        AulaRm,
        /// <summary>AULA WIN 60 HE, WIN 68 HE, KP-TE153 and Redragon K673/K617, the W669 event protocol.</summary>
        AulaW669,
        /// <summary>MADLIONS MAD 68 Pro R, the A0 stream.</summary>
        Mad68ProR,
        /// <summary>ATK Hex80, 02 96 1C matrix polling.</summary>
        AtkHex80,
        /// <summary>IROK NA87 Mag, M484 events.</summary>
        IrokNa87,
        /// <summary>AJAZZ AK820 MAX RGB, M484 raw rows.</summary>
        AjazzAk820,
        /// <summary>MonsGeek and EPOMAKER RY5088 boards, feature-report snapshots.</summary>
        RongYuanSnapshot,
        /// <summary>RY5088 boards of many brands, the report 5 event stream.</summary>
        RongYuanStream,
        /// <summary>Neo65 SONIC HE+.</summary>
        Neo65,
        /// <summary>SteelSeries Apex Pro family.</summary>
        SteelSeriesApex,
        /// <summary>MCHOSE Mix 87 III.</summary>
        MchoseMix87,
        /// <summary>IROK and EWEADN boards on SparkLink (JingTai V2 rows).</summary>
        SparkLink,
        /// <summary>SayoDevice O3C.</summary>
        Sayo,
        /// <summary>Keychron K4 HE running HallJoy's onboard firmware.</summary>
        KeychronOnboard,
    }

    /// <summary>
    /// Which HID collections are analog keyboards, what each family is called,
    /// and which keys it can report (issue #468). Pure: the app hands in the
    /// facts it read from the collection, and nothing here touches a device.
    ///
    /// <para>The identification rules are Soup's checkDeviceName and the
    /// filter lists of AnalogSense.js, cross-checked, with the Wooting product
    /// ID mask from the Wooting Analog SDK's device.rs (WOOTING_PID_MODE_MASK)
    /// and the Tartarus Pro entry from the Tartarus Pro commit on DenkiSuki's
    /// Soup fork.</para>
    /// </summary>
    public static class AnalogKeyboardCatalog
    {
        public const ushort WootingVendorId = 0x31E3;
        public const ushort LegacyWootingVendorId = 0x03EB;
        public const ushort RazerVendorId = 0x1532;
        public const ushort NuPhyVendorId = 0x19F5;
        public const ushort DrunkDeerVendorId = 0x352D;
        public const ushort KeychronVendorId = 0x3434;
        public const ushort LemokeyVendorId = 0x362D;
        public const ushort MadlionsVendorId = 0x373B;
        public const ushort BytechVendorId = 0x372E;

        /// <summary>Wooting keyboards change the low nibble of their product
        /// ID with the gamepad mode (device.rs WOOTING_PID_MODE_MASK), so one
        /// keyboard keeps one identity across modes only when it is masked.</summary>
        public const ushort WootingPidModeMask = 0xFFF0;

        /// <summary>Report IDs the Razer collections carry their analog data on.</summary>
        public const byte RazerHuntsmanV2ReportId = 7;
        public const byte RazerHuntsmanV3ReportId = 11;
        public const byte RazerTartarusProReportId = 6;
        public const byte DrunkDeerReportId = 4;
        public const byte BytechReportId = 9;

        /// <summary>Identifies the family a HID top-level collection belongs
        /// to, or <see cref="AnalogKeyboardProtocol.None"/>. <paramref name="hasInputReport"/>
        /// answers whether the collection's input reports include a given ID,
        /// the question Soup asks through HidP_InitializeReportForID.</summary>
        public static AnalogKeyboardProtocol Identify(ushort vendorId, ushort productId,
            ushort usagePage, ushort usage, Func<byte, bool> hasInputReport)
        {
            switch (vendorId)
            {
                case WootingVendorId:
                    if (usagePage == 0xFF54) return AnalogKeyboardProtocol.WootingV1;
                    if (usagePage == 0xFF53) return AnalogKeyboardProtocol.WootingV2;
                    return AnalogKeyboardProtocol.None;

                case LegacyWootingVendorId:
                    // Old firmware speaks only the v1 interface.
                    return usagePage == 0xFF54 && (productId == 0xFF01 || productId == 0xFF02)
                        ? AnalogKeyboardProtocol.WootingV1
                        : AnalogKeyboardProtocol.None;

                case RazerVendorId:
                    if (hasInputReport == null) return AnalogKeyboardProtocol.None;
                    if ((productId == 0x0266 || productId == 0x0282) && hasInputReport(RazerHuntsmanV2ReportId))
                        return AnalogKeyboardProtocol.RazerHuntsmanV2;
                    if ((productId == 0x02A6 || productId == 0x02A7 || productId == 0x02B0)
                        && hasInputReport(RazerHuntsmanV3ReportId))
                        return AnalogKeyboardProtocol.RazerHuntsmanV3;
                    if (productId == 0x0244 && hasInputReport(RazerTartarusProReportId))
                        return AnalogKeyboardProtocol.RazerTartarusPro;
                    return AnalogKeyboardProtocol.None;

                case NuPhyVendorId:
                    return usagePage == 1 && usage == 0
                        ? AnalogKeyboardProtocol.NuPhy
                        : AnalogKeyboardProtocol.None;

                case DrunkDeerVendorId:
                    return hasInputReport != null && hasInputReport(DrunkDeerReportId)
                        ? AnalogKeyboardProtocol.DrunkDeer
                        : AnalogKeyboardProtocol.None;

                case KeychronVendorId:
                case LemokeyVendorId:
                    return usagePage == 0xFF60 && usage == 0x61 && KeychronLayout(vendorId, productId) != null
                        ? AnalogKeyboardProtocol.Keychron
                        : AnalogKeyboardProtocol.None;

                case MadlionsVendorId:
                    return usagePage == 0xFF60 && usage == 0x61 && MadlionsLayout(productId) != null
                        ? AnalogKeyboardProtocol.Madlions
                        : AnalogKeyboardProtocol.None;

                case BytechVendorId:
                    return productId == 0x105B && usagePage == 0xFF00
                        ? AnalogKeyboardProtocol.Bytech
                        : AnalogKeyboardProtocol.None;
            }
            return AnalogKeyboardProtocol.None;
        }

        /// <summary>The product ID a keyboard's identity is filed under:
        /// masked for Wooting, whose product ID moves with its gamepad mode.</summary>
        public static ushort IdentityProductId(ushort vendorId, ushort productId)
            => vendorId == WootingVendorId ? (ushort)(productId & WootingPidModeMask) : productId;

        /// <summary>The model name the references give a product, or null
        /// when the product string the device reports is the better name
        /// (Wooting and NuPhy name themselves, and a DrunkDeer the references
        /// do not list does too).</summary>
        public static string ModelName(AnalogKeyboardProtocol protocol, ushort vendorId, ushort productId)
        {
            switch (protocol)
            {
                case AnalogKeyboardProtocol.WootingV1:
                    if (vendorId == LegacyWootingVendorId)
                        return productId == 0xFF01 ? "Wooting One" : "Wooting Two";
                    return null;
                case AnalogKeyboardProtocol.RazerHuntsmanV2:
                    return productId == 0x0266 ? "Razer Huntsman V2 Analog" : "Razer Huntsman Mini Analog";
                case AnalogKeyboardProtocol.RazerHuntsmanV3:
                    return productId switch
                    {
                        0x02A6 => "Razer Huntsman V3 Pro",
                        0x02A7 => "Razer Huntsman V3 Pro Tenkeyless",
                        _ => "Razer Huntsman V3 Pro Mini",
                    };
                case AnalogKeyboardProtocol.RazerTartarusPro:
                    return "Razer Tartarus Pro";
                case AnalogKeyboardProtocol.DrunkDeer:
                    return productId switch
                    {
                        0x2382 => "DrunkDeer G65",
                        // Also used by the A75 Pro and the ISO A75 (Soup).
                        0x2383 => "DrunkDeer A75",
                        0x2384 => "DrunkDeer G60",
                        0x2386 or 0x2391 => "DrunkDeer G75",
                        _ => null,
                    };
                case AnalogKeyboardProtocol.Keychron:
                    if (vendorId == LemokeyVendorId)
                        return productId == 0x0611 ? "Lemokey P1 HE ISO" : "Lemokey P1 HE";
                    return productId switch
                    {
                        0x0B10 or 0x0B11 or 0x0B12 => "Keychron Q1 HE",
                        0x0B30 => "Keychron Q3 HE",
                        0x0B50 => "Keychron Q5 HE",
                        _ => "Keychron K2 HE",
                    };
                case AnalogKeyboardProtocol.Madlions:
                    return productId switch
                    {
                        0x10A7 => "Madlions MAD68R",
                        0x1058 or 0x1059 or 0x105A or 0x105C => "Madlions MAD68HE",
                        _ => "Madlions MAD60HE",
                    };
                case AnalogKeyboardProtocol.Bytech:
                    return "Redragon K709 HE";
            }
            return null;
        }

        /// <summary>The Keychron or Lemokey layout for a product, or null when
        /// the references have none for it.</summary>
        public static AnalogKeyCodes.Layout KeychronLayout(ushort vendorId, ushort productId)
        {
            if (vendorId == LemokeyVendorId)
            {
                if (productId == 0x0610) return AnalogKeyCodes.LemokeyP1HeAnsi;
                if (productId == 0x0611) return AnalogKeyCodes.LemokeyP1HeIso;
                return null;
            }
            if (vendorId != KeychronVendorId) return null;
            return productId switch
            {
                0x0B10 or 0x0B11 or 0x0B12 => AnalogKeyCodes.KeychronQ1He,
                0x0B30 => AnalogKeyCodes.KeychronQ3He,
                0x0B50 => AnalogKeyCodes.KeychronQ5He,
                0x0E20 or 0x0E21 or 0x0E22 => AnalogKeyCodes.KeychronK2He,
                _ => null,
            };
        }

        /// <summary>The Madlions layout for a product, or null when the
        /// references have none. PID 0x1054 is a MAD60HE (Soup, after
        /// universal-analog-plugin issue #37). AnalogSense.js lists the PID
        /// but its layout check misses it and would read it as a MAD68HE.</summary>
        public static AnalogKeyCodes.Layout MadlionsLayout(ushort productId) => productId switch
        {
            0x1053 or 0x1054 or 0x1055 or 0x1056 or 0x105D => AnalogKeyCodes.MadlionsMad60He,
            0x1058 or 0x1059 or 0x105A or 0x105C or 0x10A7 => AnalogKeyCodes.MadlionsMad68He,
            _ => null,
        };

        /// <summary>The keys a family can report, in the order the picker
        /// lists them: the layout's keys for the polled families and the
        /// keypad, the full keyboard for the rest.</summary>
        public static int[] KeysFor(ushort vendorId, ushort productId)
        {
            if (vendorId == RazerVendorId && productId == 0x0244)
                return (int[])AnalogKeyCodes.TartarusPro.Clone();
            var layout = vendorId == MadlionsVendorId
                ? MadlionsLayout(productId)
                : KeychronLayout(vendorId, productId);
            if (layout != null) return AnalogKeyCodes.KeysOf(layout);
            return (int[])AnalogKeyCodes.FullKeyboard.Clone();
        }

        /// <summary>True for the families that answer requests instead of
        /// pushing reports.</summary>
        public static bool IsPolled(AnalogKeyboardProtocol protocol)
            => protocol is AnalogKeyboardProtocol.DrunkDeer or AnalogKeyboardProtocol.Keychron
                or AnalogKeyboardProtocol.Madlions or AnalogKeyboardProtocol.Bytech;

        /// <summary>True for the families that report only while Razer Synapse
        /// runs (Soup's areRazerAnalogueReportsEnabled, and reWASD's
        /// documentation says the same).</summary>
        public static bool NeedsSynapse(AnalogKeyboardProtocol protocol)
            => protocol is AnalogKeyboardProtocol.RazerHuntsmanV2 or AnalogKeyboardProtocol.RazerHuntsmanV3
                or AnalogKeyboardProtocol.RazerTartarusPro;

        /// <summary>The process names Soup checks for a running Synapse.</summary>
        public static readonly string[] SynapseProcessNames = { "RazerAppEngine", "Razer Synapse 3" };
    }
}
