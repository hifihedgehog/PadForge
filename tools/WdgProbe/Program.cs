using System;
using PadForge.Common.Input;

// Prints every ACPI-WMI _WDG entry the firmware declares (issue #343):
// GUID, flags, notify id, instance count. Diagnostic for a machine whose
// vendor hotkey never learns: the event GUID must appear here with the
// EVENT flag, or PadForge will not subscribe to its class.
var blocks = AcpiWmi.ReadBlocks();
Console.WriteLine($"_WDG entries: {blocks.Count}");
foreach (var b in blocks)
    Console.WriteLine($"{b.Guid} flags=0x{b.Flags:X2} notify=0x{b.NotifyId:X2} inst={b.InstanceCount}{(b.IsEvent ? " EVENT" : "")}");
