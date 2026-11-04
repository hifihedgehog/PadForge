using System.Management;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The PC's DMI names as OpenRGB's DMIInfo reads them on Windows (#557),
    /// for the families that pick a laptop's light table by them: the
    /// baseboard's manufacturer from Win32_BaseBoard, and the product name
    /// and SKU from Win32_ComputerSystem's Model and SystemSKUNumber, each
    /// the first object's value, or "" when the query gave none
    /// (dmiinfo/dmiinfo.cpp:27-57). Each is a WMI query that changes
    /// nothing. The test host reads none of them
    /// (<see cref="OffLimitsForTest"/>).
    /// </summary>
    internal static class HidFamilyDmi
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app. Every read then answers null.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>DMIInfo::getManufacturer, or null when the query
        /// failed.</summary>
        internal static string Manufacturer() => Read("SELECT Manufacturer FROM Win32_BaseBoard", "Manufacturer");

        /// <summary>DMIInfo::getProductName, or null when the query
        /// failed.</summary>
        internal static string ProductName() => Read("SELECT Model FROM Win32_ComputerSystem", "Model");

        /// <summary>DMIInfo::getProductSKU, or null when the query
        /// failed.</summary>
        internal static string ProductSku() => Read("SELECT SystemSKUNumber FROM Win32_ComputerSystem", "SystemSKUNumber");

        /// <summary>The first object's value of <paramref name="key"/>, ""
        /// when there is no object, as readWMIQuery answers
        /// (dmiinfo.cpp:35-57), or null when the query failed.</summary>
        private static string Read(string query, string key)
        {
            if (OffLimitsForTest) return null;
            try
            {
                using var searcher = new ManagementObjectSearcher(query);
                using var results = searcher.Get();
                foreach (ManagementBaseObject row in results)
                {
                    using (row)
                        return row[key]?.ToString() ?? "";
                }
                return "";
            }
            catch
            {
                return null;
            }
        }
    }
}
