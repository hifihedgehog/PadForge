using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Management;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The third delivery path for hidden buttons (issue #343 follow-up,
    /// found on a Legion Pro 7): keys the firmware reports to the vendor's
    /// ACPI-WMI provider rather than to any keyboard or HID collection. The
    /// Lenovo Vantage and Smart Connect keys arrive ONLY as
    /// <c>LENOVO_UTILITY_EVENT</c> instances with a <c>PressTypeDataVal</c>
    /// (72 and 1 on that machine), the same class Lenovo Legion Toolkit
    /// listens on. Vendor-agnostic on purpose: every subclass of
    /// <c>WmiEvent</c> in <c>root\WMI</c> is a candidate during a learn
    /// pass (MSI's <c>MSI_Event</c>, ASUS's ATK events, Lenovo's family),
    /// and only the classes a definition names stay subscribed otherwise.
    /// WMI subscription is blocking COM work: sweep worker only.
    /// </summary>
    internal static class WmiEventRuntime
    {
        public sealed class Event
        {
            public string ClassName;
            /// <summary>Data properties as invariant strings, the provider's
            /// bookkeeping fields (security descriptor, timestamps, instance
            /// name, Active) left out.</summary>
            public List<(string Name, string Value)> Props = new();
        }

        private const string Scope = @"root\WMI";
        private static readonly HashSet<string> Bookkeeping = new(StringComparer.OrdinalIgnoreCase)
        {
            "SECURITY_DESCRIPTOR", "TIME_CREATED", "InstanceName", "Active",
        };

        private static readonly object _lock = new();
        private static readonly Dictionary<string, ManagementEventWatcher> _watchers =
            new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Classes the firmware gate has already turned down. A
        /// refused class never enters <see cref="_watchers"/>, so without
        /// this the sweep re-asked every four seconds and re-logged the
        /// refusal forever. Cleared with the watchers, so a re-enable
        /// re-evaluates. Guarded by <see cref="_lock"/>.</summary>
        private static readonly HashSet<string> _refused =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Fires on a WMI callback thread for every subscribed class.</summary>
        public static event Action<Event> EventReceived;

        /// <summary>Class names currently subscribed.</summary>
        public static string[] Subscribed
        {
            get { lock (_lock) { var a = new string[_watchers.Count]; _watchers.Keys.CopyTo(a, 0); return a; } }
        }

        /// <summary>The event classes the FIRMWARE declares: subclasses of
        /// WmiEvent whose guid qualifier is an event entry in the ACPI-WMI
        /// <c>_WDG</c> table (<see cref="AcpiWmi"/>). Every other WMI event
        /// class on the machine belongs to a kernel driver (audio, network,
        /// storage miniports behind Microsoft class drivers) and is never
        /// touched: subscribing to one of those sent an enable request that
        /// a driver completed twice and bug-checked the bench machine
        /// (0x44). Null when WMI itself is unavailable. Worker only.</summary>
        public static List<string> EnumerateEventClasses()
        {
            lock (_lock)
            {
                if (_classCache != null && Environment.TickCount64 - _classCacheTicks < ClassCacheMs)
                    return new List<string>(_classCache);
            }
            var fresh = EnumerateEventClassesUncached();
            // Cache a real answer only. Null is "WMI unavailable" and empty is
            // "the firmware read produced nothing", which a transient
            // GetSystemFirmwareTable failure also produces: caching either one
            // would hold the whole feature closed for the cache's lifetime on
            // the strength of one bad read.
            if (fresh != null && fresh.Count > 0)
                lock (_lock) { _classCache = new List<string>(fresh); _classCacheTicks = Environment.TickCount64; }
            return fresh;
        }

        // The deep WmiEvent enumeration is a WMI round trip; the sweep asks
        // every few seconds during a capture, and the firmware table does
        // not change between reboots.
        private const int ClassCacheMs = 60_000;
        private static List<string> _classCache;
        private static long _classCacheTicks;

        private static List<string> EnumerateEventClassesUncached()
        {
            try
            {
                var firmware = AcpiWmi.ReadEventGuids();
                var result = new List<string>();
                if (firmware.Count == 0)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine("Handheld: firmware declares no ACPI-WMI event GUIDs; no WMI class is watched");
                    return result;
                }
                using var root = new ManagementClass(new ManagementScope(Scope), new ManagementPath("WmiEvent"), null);
                using var subs = root.GetSubclasses(new EnumerationOptions { EnumerateDeep = true });
                foreach (ManagementObject sub in subs)
                {
                    using (sub)
                    {
                        string name = sub.ClassPath?.ClassName;
                        if (string.IsNullOrEmpty(name)) continue;
                        Guid guid;
                        try
                        {
                            var q = sub.Qualifiers["guid"];
                            if (q == null || !Guid.TryParse(Stringify(q.Value), out guid)) continue;
                        }
                        catch { continue; }
                        if (firmware.Contains(guid)) result.Add(name);
                    }
                }
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"Handheld: firmware declares {firmware.Count} ACPI-WMI event GUIDs, {result.Count} event classes match");
                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Subscribes the wanted classes and drops the rest. Worker only.
        ///
        /// <para>The firmware gate lives HERE, not at the caller. It used to
        /// guard only the learn pass's enumeration, so a class named by a
        /// stored definition (a hand-edited PadForge.xml, a button set
        /// imported from another machine, an entry learned before the gate
        /// existed) reached <c>Start</c> ungated. That is the exact path
        /// whose blanket version bug-checked the bench machine with
        /// MULTIPLE_IRP_COMPLETE_REQUESTS: a user-mode subscription to a
        /// kernel driver's WMI class can crash the kernel. A guard that
        /// covers one of an operation's callers is not a guard, so it now
        /// covers the operation and a caller written later inherits it.</para></summary>
        public static void Sync(HashSet<string> wanted)
        {
            wanted ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ManagementEventWatcher> stop = null;
            var start = new List<string>();
            lock (_lock)
            {
                List<string> gone = null;
                foreach (var kv in _watchers)
                    if (!wanted.Contains(kv.Key)) (gone ??= new List<string>()).Add(kv.Key);
                if (gone != null)
                    foreach (var key in gone)
                    {
                        (stop ??= new List<ManagementEventWatcher>()).Add(_watchers[key]);
                        _watchers.Remove(key);
                    }
                foreach (var cls in wanted)
                    if (!_watchers.ContainsKey(cls) && !_refused.Contains(cls)) start.Add(cls);
            }
            // Only pay the enumeration when something new is about to be
            // subscribed: in the steady state every wanted class already has
            // a watcher and this costs nothing.
            if (start.Count > 0)
            {
                var allowed = EnumerateEventClasses();
                var pass = new List<string>(start.Count);
                foreach (var cls in start)
                {
                    if (allowed != null && allowed.Contains(cls, StringComparer.OrdinalIgnoreCase))
                    { pass.Add(cls); continue; }
                    lock (_lock) _refused.Add(cls);
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"Handheld: refusing to watch WMI class {cls}, the firmware does not declare its GUID as an ACPI-WMI event");
                }
                start = pass;
            }
            if (stop != null)
            {
                foreach (var w in stop) StopWatcher(w);
                PadForge.Engine.SdlDiagLog.WriteLine($"Handheld: stopped watching {stop.Count} WMI event classes; still watching {string.Join(", ", Subscribed)}");
            }
            foreach (var cls in start)
            {
                ManagementEventWatcher w = null;
                try
                {
                    w = new ManagementEventWatcher(new ManagementScope(Scope), new EventQuery("SELECT * FROM " + cls));
                    string captured = cls;
                    w.EventArrived += (s, e) => OnArrived(captured, e);
                    w.Start();
                    bool keep;
                    lock (_lock)
                    {
                        keep = !_watchers.ContainsKey(cls);
                        if (keep) _watchers[cls] = w;
                    }
                    if (!keep) StopWatcher(w);
                    else PadForge.Engine.SdlDiagLog.WriteLine("Handheld: watching WMI event class " + cls);
                }
                catch (Exception ex)
                {
                    // A class that refuses subscription (access denied, no
                    // provider instance) is simply not a source here.
                    PadForge.Engine.SdlDiagLog.WriteLine($"Handheld: cannot watch WMI class {cls}: {ex.Message}");
                    if (w != null) StopWatcher(w);
                }
            }
        }

        public static void StopAll()
        {
            List<ManagementEventWatcher> stop;
            lock (_lock)
            {
                stop = new List<ManagementEventWatcher>(_watchers.Values);
                _watchers.Clear();
                // A refusal is only as good as the firmware read behind it,
                // and a re-enable re-reads. Holding refusals across a full
                // stop would make one bad read permanent for the process.
                _refused.Clear();
            }
            foreach (var w in stop) StopWatcher(w);
        }

        private static void StopWatcher(ManagementEventWatcher w)
        {
            try { w.Stop(); } catch { }
            try { w.Dispose(); } catch { }
        }

        private static void OnArrived(string cls, EventArrivedEventArgs e)
        {
            var ev = new Event { ClassName = cls };
            // ManagementBaseObject wraps a COM object. Left to the finalizer
            // it is one queued release per event, for the life of the row.
            using (var incoming = e.NewEvent)
            {
                try
                {
                    foreach (PropertyData p in incoming.Properties)
                    {
                        if (p == null || Bookkeeping.Contains(p.Name)) continue;
                        ev.Props.Add((p.Name, Stringify(p.Value)));
                    }
                }
                catch { }
            }
            // Sparse: firmware keys arrive at human rate, so every event is
            // worth a line for a "learned but never fires" report.
            var parts = new List<string>(ev.Props.Count);
            foreach (var (n, v) in ev.Props) parts.Add(n + "=" + v);
            PadForge.Engine.SdlDiagLog.WriteLine($"Handheld: WMI event {cls} {string.Join(" ", parts)}");
            try { EventReceived?.Invoke(ev); } catch { }
        }

        /// <summary>Invariant text for a property value, so a learned value
        /// compares equal across sessions and machines.</summary>
        public static string Stringify(object value)
        {
            if (value == null) return string.Empty;
            if (value is Array arr)
            {
                var parts = new List<string>(arr.Length);
                foreach (var item in arr) parts.Add(Stringify(item));
                return string.Join(",", parts);
            }
            if (value is IFormattable f) return f.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }

        /// <summary>Test seam: deliver an event as a provider would.</summary>
        internal static void RaiseForTest(Event ev) => EventReceived?.Invoke(ev);
    }
}
