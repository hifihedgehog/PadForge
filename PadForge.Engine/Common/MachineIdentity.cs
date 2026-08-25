using System;
using Microsoft.Win32;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// The machine's SMBIOS identity, read from the registry mirror the
    /// kernel keeps under HKLM\HARDWARE\DESCRIPTION\System\BIOS (issue #343).
    /// The same strings Handheld Companion and InputPlumber match their
    /// device tables on (manufacturer, product name, family, board), read
    /// without WMI so the poll-side code never pays for a COM round trip.
    /// </summary>
    public sealed class MachineIdentity
    {
        public string Manufacturer { get; init; } = string.Empty;
        public string ProductName { get; init; } = string.Empty;
        public string Family { get; init; } = string.Empty;
        public string BoardProduct { get; init; } = string.Empty;
        public string Sku { get; init; } = string.Empty;

        /// <summary>Stable key a definition is filed under: manufacturer and
        /// product, upper-cased and trimmed, joined with a bar.</summary>
        public string Key => $"{Norm(Manufacturer)}|{Norm(ProductName)}";

        /// <summary>Display name for the Devices page: family when the
        /// product name is a bare model code, product otherwise.</summary>
        public string DisplayName
        {
            get
            {
                string p = ProductName?.Trim() ?? string.Empty;
                string f = Family?.Trim() ?? string.Empty;
                if (f.Length > 0 && (p.Length == 0 || LooksLikeCode(p))) return f;
                return p.Length > 0 ? p : (f.Length > 0 ? f : "This PC");
            }
        }

        private static bool LooksLikeCode(string s)
        {
            // "83RU", "LNVNB161216": short, no spaces, letters and digits mixed.
            if (s.Length > 12 || s.Contains(' ')) return false;
            bool digit = false, letter = false;
            foreach (char c in s) { if (char.IsDigit(c)) digit = true; else if (char.IsLetter(c)) letter = true; }
            return digit && letter;
        }

        public static string Norm(string s) => (s ?? string.Empty).Trim().ToUpperInvariant();

        private static MachineIdentity _current;

        /// <summary>Reads once and caches. Never throws; missing values read
        /// as empty strings.</summary>
        public static MachineIdentity Current => _current ??= Read();

        internal static MachineIdentity Read()
        {
            string manu = "", prod = "", fam = "", board = "", sku = "";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                if (key != null)
                {
                    manu = key.GetValue("SystemManufacturer") as string ?? "";
                    prod = key.GetValue("SystemProductName") as string ?? "";
                    fam = key.GetValue("SystemFamily") as string ?? "";
                    board = key.GetValue("BaseBoardProduct") as string ?? "";
                    sku = key.GetValue("SystemSKU") as string ?? "";
                }
            }
            catch { }
            return new MachineIdentity
            {
                Manufacturer = manu, ProductName = prod, Family = fam, BoardProduct = board, Sku = sku,
            };
        }

        /// <summary>Test seam.</summary>
        internal static void SetForTest(MachineIdentity id) => _current = id;
    }
}
