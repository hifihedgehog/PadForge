using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace PadForge.Services.Automation.Web
{
    /// <summary>
    /// Reach PadForge From the Web in PadForge.xml (#600 item 11): the
    /// switch, the tunnel picked, a pasted address, the loopback port a
    /// user's own tunnel points at, and the web assistants allowed, each
    /// with its tokens as SHA-256 hashes only. Written only while it holds
    /// something.
    /// </summary>
    public sealed class WebAccessData
    {
        [XmlAttribute] public bool On { get; set; }

        /// <summary>The tunnel picked: a tunnel program's key, "paste" or
        /// empty for none.</summary>
        [XmlAttribute] public string Tunnel { get; set; }

        /// <summary>The address the user pasted.</summary>
        [XmlAttribute] public string Address { get; set; }

        /// <summary>The loopback port the helper took, kept so a tunnel the
        /// user runs keeps pointing at it.</summary>
        [XmlAttribute] public int Port { get; set; }

        [XmlElement("Assistant")]
        public WebAssistantData[] Assistants { get; set; }

        internal bool IsEmpty => !On && string.IsNullOrEmpty(Tunnel) && string.IsNullOrEmpty(Address) && Port == 0
                                 && (Assistants == null || Assistants.Length == 0);
    }

    /// <summary>One allowed web assistant in PadForge.xml.</summary>
    public sealed class WebAssistantData
    {
        [XmlAttribute] public string Id { get; set; }
        [XmlAttribute] public string Origin { get; set; }
        [XmlAttribute] public string Name { get; set; }
        [XmlAttribute] public string ClientId { get; set; }
        [XmlAttribute] public string Redirect { get; set; }
        [XmlAttribute] public string Resource { get; set; }

        /// <summary>SHA-256 of its access token, lower hex. The token itself
        /// is never written.</summary>
        [XmlAttribute] public string AccessHash { get; set; }
        [XmlAttribute] public DateTime AccessUntil { get; set; }

        /// <summary>SHA-256 of its refresh token, lower hex.</summary>
        [XmlAttribute] public string RefreshHash { get; set; }
        [XmlAttribute] public DateTime RefreshUntil { get; set; }

        [XmlAttribute] public DateTime Allowed { get; set; }
        [XmlAttribute] public DateTime LastUsed { get; set; }
    }

    /// <summary>
    /// The web door's settings and allowed web assistants in memory (#600
    /// item 11), loaded from PadForge.xml and saved back to it, as the
    /// allowed programs are (AutomationConsent.Shared). Changed is raised on
    /// any change, and the window marks the file to be saved.
    /// </summary>
    internal sealed class WebAccessStore
    {
        /// <summary>The most web assistants kept.</summary>
        internal const int MostAssistants = 8;

        internal static WebAccessStore Shared { get; } = new WebAccessStore();

        private readonly object _sync = new object();
        private readonly List<WebGrant> _grants = new();

        /// <summary>Raised on a change the user or a sign-in made, which the
        /// file must keep.</summary>
        public event EventHandler Changed;

        /// <summary>Raised when the file's values were read in, which the
        /// file already holds.</summary>
        public event EventHandler Loaded;

        public bool On { get; private set; }
        public string Tunnel { get; private set; }
        public string Address { get; private set; }
        public int Port { get; private set; }

        public IReadOnlyList<WebGrant> Grants
        {
            get { lock (_sync) return _grants.ToList(); }
        }

        internal void Load(WebAccessData data)
        {
            lock (_sync)
            {
                _grants.Clear();
                On = data?.On ?? false;
                Tunnel = string.IsNullOrEmpty(data?.Tunnel) ? null : data.Tunnel;
                Address = string.IsNullOrEmpty(data?.Address) ? null : data.Address;
                Port = data?.Port is > 1024 and < 65536 ? data.Port : 0;
                foreach (var a in data?.Assistants ?? Array.Empty<WebAssistantData>())
                {
                    if (string.IsNullOrEmpty(a?.Id) || string.IsNullOrEmpty(a.Origin) || _grants.Count >= MostAssistants) continue;
                    _grants.Add(new WebGrant
                    {
                        Id = a.Id, Origin = a.Origin, Name = string.IsNullOrEmpty(a.Name) ? null : a.Name,
                        ClientId = a.ClientId, RedirectUri = a.Redirect, Resource = a.Resource,
                        AccessHash = Hex(a.AccessHash), AccessUntil = Utc(a.AccessUntil),
                        RefreshHash = Hex(a.RefreshHash), RefreshUntil = Utc(a.RefreshUntil),
                        Allowed = Utc(a.Allowed), LastUsed = a.LastUsed == default ? null : Utc(a.LastUsed),
                    });
                }
            }
            Loaded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>What goes into PadForge.xml, or null when nothing
        /// does.</summary>
        internal WebAccessData Save()
        {
            lock (_sync)
            {
                var data = new WebAccessData
                {
                    On = On,
                    Tunnel = Tunnel,
                    Address = Address,
                    Port = Port,
                    Assistants = _grants.Count == 0 ? null : _grants.Select(g => new WebAssistantData
                    {
                        Id = g.Id, Origin = g.Origin, Name = g.Name, ClientId = g.ClientId, Redirect = g.RedirectUri, Resource = g.Resource,
                        AccessHash = g.AccessHash, AccessUntil = g.AccessUntil.UtcDateTime,
                        RefreshHash = g.RefreshHash, RefreshUntil = g.RefreshUntil.UtcDateTime,
                        Allowed = g.Allowed.UtcDateTime, LastUsed = g.LastUsed?.UtcDateTime ?? default,
                    }).ToArray(),
                };
                return data.IsEmpty ? null : data;
            }
        }

        internal void SetOn(bool on) => Change(() => { if (On == on) return false; On = on; return true; });

        internal void SetTunnel(string tunnel, string address)
            => Change(() =>
            {
                if (Tunnel == tunnel && Address == address) return false;
                Tunnel = tunnel;
                Address = address;
                return true;
            });

        internal void SetPort(int port) => Change(() => { if (Port == port) return false; Port = port; return true; });

        internal bool Add(WebGrant grant)
        {
            bool added = false;
            Change(() =>
            {
                if (_grants.Count >= MostAssistants) return false;
                _grants.Add(grant);
                added = true;
                return true;
            });
            return added;
        }

        /// <summary>Takes in new token hashes from the helper, for a grant
        /// that is still allowed.</summary>
        internal void Tokens(WebGrant from)
            => Change(() =>
            {
                int i = _grants.FindIndex(g => g.Id == from.Id);
                if (i < 0) return false;
                _grants[i] = _grants[i] with
                {
                    AccessHash = Hex(from.AccessHash), AccessUntil = from.AccessUntil,
                    RefreshHash = Hex(from.RefreshHash), RefreshUntil = from.RefreshUntil,
                    LastUsed = from.LastUsed ?? _grants[i].LastUsed,
                };
                return true;
            });

        internal void Used(string id, DateTimeOffset when)
            => Change(() =>
            {
                int i = _grants.FindIndex(g => g.Id == id);
                if (i < 0) return false;
                _grants[i] = _grants[i] with { LastUsed = when };
                return true;
            });

        internal bool Revoke(string id)
        {
            bool removed = false;
            Change(() => removed = _grants.RemoveAll(g => g.Id == id) > 0);
            return removed;
        }

        private void Change(Func<bool> change)
        {
            bool changed;
            lock (_sync) changed = change();
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }

        private static DateTimeOffset Utc(DateTime when)
            => when == default ? DateTimeOffset.MinValue : new DateTimeOffset(DateTime.SpecifyKind(when, DateTimeKind.Utc));

        /// <summary>A hash as it is kept: 64 lower hex digits, else none.</summary>
        private static string Hex(string hash)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length != 64) return null;
            foreach (char c in hash)
                if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return null;
            return hash;
        }
    }
}
