using System;
using System.Collections.Generic;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// What the app reads from one HID top-level collection before any route
    /// sends it a byte (issue #468): the attributes, the capabilities, the
    /// strings and which report IDs the collection declares. Routes decide
    /// from this alone whether a collection is worth a handshake, so a route
    /// never writes to a device whose metadata it does not already recognize.
    /// </summary>
    public sealed class AnalogKeyboardDeviceInfo
    {
        /// <summary>The collection's interface path.</summary>
        public string Path = string.Empty;

        public ushort VendorId;
        public ushort ProductId;

        /// <summary>bcdDevice, HIDD_ATTRIBUTES.VersionNumber.</summary>
        public ushort VersionNumber;

        public ushort UsagePage;
        public ushort Usage;

        /// <summary>The USB interface number from the path's "mi_XX", or -1
        /// when the path names none (a single-interface device).</summary>
        public int InterfaceNumber = -1;

        /// <summary>Windows lengths, report ID byte included.</summary>
        public ushort InputReportLength;
        public ushort OutputReportLength;
        public ushort FeatureReportLength;

        public string ProductString = string.Empty;
        public string ManufacturerString = string.Empty;
        public string SerialNumber = string.Empty;

        /// <summary>DEVPKEY_Device_ContainerId: every collection of one
        /// physical device shares it. Empty when Windows reports none.</summary>
        public string ContainerId = string.Empty;

        /// <summary>Whether the collection declares an input, output or
        /// feature report with a given ID (HidP_InitializeReportForID).
        /// Report ID 0 answers for a collection that numbers no reports.</summary>
        public Func<byte, bool> HasInputReport = _ => false;
        public Func<byte, bool> HasOutputReport = _ => false;
        public Func<byte, bool> HasFeatureReport = _ => false;

        /// <summary>The input value caps (HidP_GetValueCaps), for the routes
        /// that recognize a collection by its report layout.</summary>
        public IReadOnlyList<AnalogKeyboardValueCap> InputValueCaps = Array.Empty<AnalogKeyboardValueCap>();

        /// <summary>The other collections of the same physical device (same
        /// VID, PID and container ID), for the routes that read one
        /// collection and command another. Filled by the enumerator.</summary>
        public IReadOnlyList<AnalogKeyboardDeviceInfo> Siblings = Array.Empty<AnalogKeyboardDeviceInfo>();

        /// <summary>Product string trimmed and upper-cased, the form most
        /// route checks compare against.</summary>
        public string ProductKey => (ProductString ?? string.Empty).Trim().ToUpperInvariant();
    }

    /// <summary>One HIDP_VALUE_CAPS entry, the fields routes check.</summary>
    public readonly struct AnalogKeyboardValueCap
    {
        public AnalogKeyboardValueCap(byte reportId, ushort usagePage, ushort bitSize, ushort reportCount)
        {
            ReportId = reportId;
            UsagePage = usagePage;
            BitSize = bitSize;
            ReportCount = reportCount;
        }

        public byte ReportId { get; }
        public ushort UsagePage { get; }
        public ushort BitSize { get; }
        public ushort ReportCount { get; }
    }
}
