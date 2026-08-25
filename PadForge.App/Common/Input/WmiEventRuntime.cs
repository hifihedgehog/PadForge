using System;
using System.Collections.Generic;
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

        /// <summary>Fires on a WMI callback thread for every subscribed class.</summary>
        public static event Action<Event> EventReceived;

        /// <summary>Class names currently subscribed.</summary>
        public static string[] Subscribed
        {
            get { lock (_lock) { var a = new string[_watchers.Count]; _watchers.Keys.CopyTo(a, 0); return a; } }
        }

        /// <summary>Every extrinsic event class the machine's WMI providers
        /// expose (subclasses of WmiEvent, deep). Null when WMI itself is
        /// unavailable. Worker only.</summary>
        public static List<string> EnumerateEventClasses()
        {
            try
            {
                var result = new List<string>();
                using var root = new ManagementClass(new ManagementScope(Scope), new ManagementPath("WmiEvent"), null);
                using var subs = root.GetSubclasses(new EnumerationOptions { EnumerateDeep = true });
                foreach (ManagementObject sub in subs)
                {
                    using (sub)
                    {
                        string name = sub.ClassPath?.ClassName;
                        if (!string.IsNullOrEmpty(name)) result.Add(name);
                    }
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Subscribes the wanted classes and drops the rest. Worker only.</summary>
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
                    if (!_watchers.ContainsKey(cls)) start.Add(cls);
            }
            if (stop != null)
                foreach (var w in stop) StopWatcher(w);
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
            try
            {
                foreach (PropertyData p in e.NewEvent.Properties)
                {
                    if (p == null || Bookkeeping.Contains(p.Name)) continue;
                    ev.Props.Add((p.Name, Stringify(p.Value)));
                }
            }
            catch { }
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
