using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Common.Input;

// Runs PadForge's own WMI learner path outside the app (issue #343):
// the firmware _WDG table, the event classes that pass the gate, then a
// live subscription that prints every event for the given seconds.
int seconds = args.Length > 0 && int.TryParse(args[0], out int s) ? s : 0;

var blocks = AcpiWmi.ReadBlocks();
Console.WriteLine($"_WDG entries: {blocks.Count}");
foreach (var b in blocks)
    Console.WriteLine($"  {b.Guid} flags=0x{b.Flags:X2} notify=0x{b.NotifyId:X2} inst={b.InstanceCount}{(b.IsEvent ? " EVENT" : "")}");

var classes = WmiEventRuntime.EnumerateEventClasses();
Console.WriteLine(classes == null ? "EnumerateEventClasses: null (WMI unavailable)" : $"event classes passing the gate: {classes.Count}");
if (classes != null) foreach (var c in classes) Console.WriteLine("  " + c);

if (seconds <= 0 || classes == null || classes.Count == 0) return;
WmiEventRuntime.EventReceived += ev =>
{
    var parts = new List<string>();
    foreach (var (n, v) in ev.Props) parts.Add($"{n}={v}");
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} EVENT {ev.ClassName} {string.Join(" ", parts)}");
};
WmiEventRuntime.Sync(new HashSet<string>(classes, StringComparer.OrdinalIgnoreCase));
Console.WriteLine($"subscribed: {string.Join(", ", WmiEventRuntime.Subscribed)}");
Console.WriteLine($"listening {seconds} s, press the keys now");
Thread.Sleep(seconds * 1000);
WmiEventRuntime.StopAll();
Console.WriteLine("done");
