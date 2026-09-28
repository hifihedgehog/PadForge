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

        /// <summary>HidD_SetNumInputBuffers after opening, 0 to leave the
        /// Windows default.</summary>
        public int InputBuffers { get; init; }

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
            foreach (var route in SoupFamilies()) _routes.Add(route);
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

        private static IEnumerable<AnalogKeyboardRoute> SoupFamilies()
        {
            AnalogKeyboardRoute Pushed(string id, AnalogKeyboardProtocol protocol) => new()
            {
                Id = id,
                Protocol = protocol,
                Matches = info => Identify(info) == protocol,
                CreateSession = info => new PushedReportSession(protocol, info.VendorId, info.ProductId),
                Writable = false,
                Name = info => AnalogKeyboardCatalog.ModelName(protocol, info.VendorId, info.ProductId),
                Keys = info => AnalogKeyboardCatalog.KeysFor(info.VendorId, info.ProductId),
            };
            AnalogKeyboardRoute Polled(string id, AnalogKeyboardProtocol protocol) => new()
            {
                Id = id,
                Protocol = protocol,
                Matches = info => Identify(info) == protocol,
                CreateSession = info => AnalogKeyboardPoller.Create(protocol, info.VendorId, info.ProductId),
                Name = info => AnalogKeyboardCatalog.ModelName(protocol, info.VendorId, info.ProductId),
                Keys = info => AnalogKeyboardCatalog.KeysFor(info.VendorId, info.ProductId),
            };

            yield return Pushed("soup-wooting-v2", AnalogKeyboardProtocol.WootingV2);
            yield return Pushed("soup-wooting-v1", AnalogKeyboardProtocol.WootingV1);
            yield return Pushed("soup-razer-huntsman-v2", AnalogKeyboardProtocol.RazerHuntsmanV2);
            yield return Pushed("soup-razer-huntsman-v3", AnalogKeyboardProtocol.RazerHuntsmanV3);
            yield return Pushed("soup-razer-tartarus-pro", AnalogKeyboardProtocol.RazerTartarusPro);
            yield return Pushed("soup-nuphy", AnalogKeyboardProtocol.NuPhy);
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
