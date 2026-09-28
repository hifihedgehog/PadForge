using System;
using System.Collections.Generic;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// One way of reading one family of analog keyboards (issue #468): which
    /// HID collections it recognizes from their metadata alone, how the app
    /// opens them, and the session that talks to them. A route never writes
    /// to a collection its <see cref="Matches"/> did not accept, so keyboards
    /// of other makes never see its commands.
    /// </summary>
    public sealed class AnalogKeyboardRoute
    {
        /// <summary>Stable id, for logs and tests.</summary>
        public string Id { get; init; } = string.Empty;

        public AnalogKeyboardProtocol Protocol { get; init; }

        /// <summary>Metadata-only test of one collection: attributes, caps,
        /// strings and declared report IDs. No I/O.</summary>
        public Func<AnalogKeyboardDeviceInfo, bool> Matches { get; init; } = _ => false;

        /// <summary>A fresh session for an accepted collection.</summary>
        public Func<AnalogKeyboardDeviceInfo, AnalogKeyboardSession> CreateSession { get; init; } = _ => null;

        /// <summary>Open for writing too. The families that only listen leave
        /// write access to the vendor's software.</summary>
        public bool Writable { get; init; } = true;

        /// <summary>Open with no sharing, for the protocols whose answers
        /// carry no tag and would interleave with another program's.</summary>
        public bool Exclusive { get; init; }

        /// <summary>For an exclusive route: when the open fails because another
        /// handle holds the collection (error 32) or access is denied (error
        /// 5), open it shared for reading and writing, then with no access
        /// rights, which still carries feature reports. HallJoy's ATTACK SHARK
        /// open ladder (attackshark_pro_diagnostic.cpp:228-235).</summary>
        public bool OpenFallback { get; init; }

        /// <summary>Longest wait for one WriteFile, 0 for the channel's
        /// default of 1000 ms.</summary>
        public int WriteTimeoutMs { get; init; }

        /// <summary>Longest wait for one feature or control transfer, 0 for
        /// the channel's default of 500 ms.</summary>
        public int TransferTimeoutMs { get; init; }

        /// <summary>Keys read 0 once no pass has produced a key set for this
        /// long, 0 for never. For the routes whose depths expire by wall clock,
        /// so a pass stuck on a slow transfer does not hold the last depths.
        /// On these routes an unanswered pass keeps the last key set until
        /// this runs out, as their references keep a key until it goes stale,
        /// instead of releasing every key at once.</summary>
        public int StaleAfterMs { get; init; }

        /// <summary>How long a stop waits for the route's teardown before the
        /// I/O is canceled, 0 for the device's default of 1500 ms. For the
        /// routes whose teardown takes many exchanges.</summary>
        public int StopTimeoutMs { get; init; }

        /// <summary>HidD_SetNumInputBuffers after opening, 0 to leave the
        /// Windows default.</summary>
        public int InputBuffers { get; init; }

        /// <summary>For a route that recognizes its keyboards by their
        /// reports alone and sends nothing: the row appears once a pass first
        /// produces a key set, and until then the collection is only listened
        /// to.</summary>
        public bool RegisterOnFirstReport { get; init; }

        /// <summary>When the route's handshake fails and no other route
        /// recognizes the keyboard, try again after this long instead of
        /// leaving the keyboard alone while it stays plugged in: the wait the
        /// reference's worker takes after an attempt that ran no session. 0
        /// for never. Only the routes that asked for the retry run again, and
        /// a session that set <see cref="AnalogKeyboardSession.NoStartRetry"/>
        /// cancels its route's.</summary>
        public int StartRetryMs { get; init; }

        /// <summary>For the routes whose reference probes a keyboard once and
        /// afterward reopens only the keyboards it has claimed: a keyboard
        /// this route never opened gets one handshake per plug-in, and
        /// <see cref="StartRetryMs"/> applies only to a keyboard it opened or
        /// recognized before.</summary>
        public bool ProbeOnce { get; init; }

        /// <summary>After a session that ran ends while the collection is
        /// still present, reopen it after this long: the wait the reference's
        /// worker takes after a session. The reopen tries only this route. 0
        /// for the default minute.</summary>
        public int ReconnectMs { get; init; }

        /// <summary>How many failed reopens in a row a keyboard this route
        /// claimed gets before it is left alone until plugged in again, for a
        /// reference that stops retrying. 0 for no limit.</summary>
        public int ReconnectTries { get; init; }

        /// <summary>For the routes that command one collection and read
        /// another of the same keyboard: picks the collection to read from
        /// among <see cref="AnalogKeyboardDeviceInfo.Siblings"/>. Null reads
        /// the matched collection itself.</summary>
        public Func<AnalogKeyboardDeviceInfo, AnalogKeyboardDeviceInfo> Companion { get; init; }

        /// <summary>The name the row carries when the session names no model,
        /// from metadata. Null falls back to the product string.</summary>
        public Func<AnalogKeyboardDeviceInfo, string> Name { get; init; }

        /// <summary>The keys the picker lists when the session gives none,
        /// from metadata. Null lists the full keyboard.</summary>
        public Func<AnalogKeyboardDeviceInfo, int[]> Keys { get; init; }
    }

    /// <summary>
    /// Every route, in the order a collection is offered to them. A keyboard
    /// can match several routes on metadata (HallJoy's ATTACK SHARK, MonsGeek
    /// and RongYuan stream routes all accept 3151:5030), so the device tries
    /// them in this order and keeps the first whose handshake succeeds. The
    /// order is HallJoy's native catalog order, then the Soup and AnalogSense
    /// families, the order in which HallJoy lets its native routes claim a
    /// collection before its Universal Analog Plugin sees it.
    /// </summary>
    public static class AnalogKeyboardRoutes
    {
        private static readonly List<AnalogKeyboardRoute> _routes = new();
        private static readonly object _lock = new();

        static AnalogKeyboardRoutes()
        {
            // HallJoy's native routes in its catalog order
            // (native_analog_backends.def), then the Soup and AnalogSense
            // families, then the listener that claims only what nothing else
            // applies to.
            _routes.Add(RongYuanRoutes.AttackShark);
            _routes.Add(Take(AulaEventsRoutes.All, AulaEventsRoutes.Mini60Id));
            _routes.Add(Take(MadlionsRoutes.All, "halljoy-mad68-a0"));
            _routes.Add(Take(MadlionsRoutes.All, "halljoy-hex80-0x96"));
            foreach (var route in Routes.AddressedRoutes.All) _routes.Add(route);
            _routes.Add(Routes.JingTaiRoutes.AulaRm);
            _routes.Add(Take(MadlionsRoutes.All, "halljoy-irok-na87-m484"));
            // The same controller on boards whose identity the NA87 route
            // turns down (KeyAxis).
            _routes.Add(KeyAxisRoute.Route);
            _routes.Add(Take(AulaEventsRoutes.All, AulaEventsRoutes.W669Id));
            _routes.Add(Routes.JingTaiRoutes.JingTaiV1);
            _routes.Add(Routes.JingTaiRoutes.ChilkeySlice75);
            _routes.Add(RongYuanRoutes.Snapshot);
            _routes.Add(RongYuanRoutes.Stream);
            foreach (var route in NeoApexMixRoutes.All) _routes.Add(route);
            foreach (var route in SparkSayoRoutes.All) _routes.Add(route);
            // Families no HallJoy route covers, each on its own identity.
            foreach (var route in OtherRoutes.All) _routes.Add(route);
            // The NuPhy protocol family, where Soup's NuPhy reader stood.
            foreach (var route in Routes.NuPhyRoutes.All) _routes.Add(route);
            foreach (var route in SoupFamilies()) _routes.Add(route);
            _routes.Add(A0ListenRoute.Route);
        }

        private static AnalogKeyboardRoute Take(IReadOnlyList<AnalogKeyboardRoute> group, string id)
        {
            foreach (var route in group)
                if (route.Id == id) return route;
            throw new InvalidOperationException("Analog keyboard route missing: " + id);
        }

        /// <summary>Every route in priority order.</summary>
        public static IReadOnlyList<AnalogKeyboardRoute> All
        {
            get { lock (_lock) return _routes.ToArray(); }
        }

        /// <summary>Adds routes ahead of the Soup and AnalogSense families,
        /// after any added before them, keeping each call's order.</summary>
        public static void RegisterNative(params AnalogKeyboardRoute[] routes)
        {
            lock (_lock)
            {
                int at = _routes.FindIndex(r => r.Id.StartsWith("soup-", StringComparison.Ordinal));
                if (at < 0) at = _routes.Count;
                foreach (var route in routes)
                {
                    if (route == null || _routes.Exists(r => r.Id == route.Id)) continue;
                    _routes.Insert(at++, route);
                }
            }
        }

        /// <summary>The routes whose metadata test accepts
        /// <paramref name="info"/>, in priority order.</summary>
        public static List<AnalogKeyboardRoute> Candidates(AnalogKeyboardDeviceInfo info)
        {
            var result = new List<AnalogKeyboardRoute>();
            if (info == null) return result;
            foreach (var route in All)
            {
                bool match;
                try { match = route.Matches(info); }
                catch { match = false; }
                if (match) result.Add(route);
            }
            return result;
        }

        /// <summary>The route with <paramref name="id"/>, or null.</summary>
        public static AnalogKeyboardRoute Find(string id)
        {
            foreach (var route in All)
                if (route.Id == id) return route;
            return null;
        }

        /// <summary>The Soup and AnalogSense families. Soup's plugin host finds
        /// keyboards again every second, and a keyboard whose reader marked
        /// it disconnected is found again there (universal-analog-plugin
        /// main.cpp:180-199, 288-299), so a failed handshake and an ended
        /// session are both tried again after a second.</summary>
        private static IEnumerable<AnalogKeyboardRoute> SoupFamilies()
        {
            AnalogKeyboardRoute Pushed(string id, AnalogKeyboardProtocol protocol) => new()
            {
                Id = id,
                Protocol = protocol,
                Matches = info => Identify(info) == protocol,
                CreateSession = info => new PushedReportSession(protocol, info.VendorId, info.ProductId),
                Writable = false,
                ReconnectMs = 1000,
                Name = info => AnalogKeyboardCatalog.ModelName(protocol, info.VendorId, info.ProductId),
                Keys = info => AnalogKeyboardCatalog.KeysFor(info.VendorId, info.ProductId),
            };
            AnalogKeyboardRoute Polled(string id, AnalogKeyboardProtocol protocol) => new()
            {
                Id = id,
                Protocol = protocol,
                Matches = info => Identify(info) == protocol,
                CreateSession = info => AnalogKeyboardPoller.Create(protocol, info.VendorId, info.ProductId),
                StartRetryMs = 1000,
                ReconnectMs = 1000,
                Name = info => AnalogKeyboardCatalog.ModelName(protocol, info.VendorId, info.ProductId),
                Keys = info => AnalogKeyboardCatalog.KeysFor(info.VendorId, info.ProductId),
            };

            yield return Pushed("soup-wooting-v2", AnalogKeyboardProtocol.WootingV2);
            yield return Pushed("soup-wooting-v1", AnalogKeyboardProtocol.WootingV1);
            yield return Pushed("soup-razer-huntsman-v2", AnalogKeyboardProtocol.RazerHuntsmanV2);
            yield return Pushed("soup-razer-huntsman-v3", AnalogKeyboardProtocol.RazerHuntsmanV3);
            yield return Pushed("soup-razer-tartarus-pro", AnalogKeyboardProtocol.RazerTartarusPro);
            yield return Polled("soup-drunkdeer", AnalogKeyboardProtocol.DrunkDeer);
            yield return Polled("soup-keychron", AnalogKeyboardProtocol.Keychron);
            yield return Polled("soup-madlions", AnalogKeyboardProtocol.Madlions);
            yield return Polled("soup-bytech", AnalogKeyboardProtocol.Bytech);
        }

        private static AnalogKeyboardProtocol Identify(AnalogKeyboardDeviceInfo info)
            => AnalogKeyboardCatalog.Identify(info.VendorId, info.ProductId, info.UsagePage, info.Usage,
                info.HasInputReport);
    }
}
