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

        /// <summary>The runtime key of the ChatGPT Secure MCP Tunnel (#600
        /// DESIGN-2 N4), encrypted by the web helper for the signed-in user
        /// alone (DPAPI CurrentUser), base64. The key itself is never
        /// written.</summary>
        [XmlAttribute] public string RuntimeKey { get; set; }

        /// <summary>That tunnel's ID, from OpenAI's tunnel management.</summary>
        [XmlAttribute] public string TunnelId { get; set; }

        [XmlElement("Assistant")]
        public WebAssistantData[] Assistants { get; set; }

        /// <summary>Reach PadForge From This Network (#600 item 53).</summary>
        [XmlAttribute] public bool Network { get; set; }

        /// <summary>The port the network door was given, kept so the
        /// reservation, the rule and each assistant's line keep naming
        /// it.</summary>
        [XmlAttribute] public int NetworkPort { get; set; }

        [XmlElement("NetworkKey")]
        public NetworkKeyData[] Keys { get; set; }

        internal bool IsEmpty => !On && string.IsNullOrEmpty(Tunnel) && string.IsNullOrEmpty(Address) && Port == 0
                                 && string.IsNullOrEmpty(RuntimeKey) && string.IsNullOrEmpty(TunnelId)
                                 && (Assistants == null || Assistants.Length == 0)
                                 && !Network && NetworkPort == 0 && (Keys == null || Keys.Length == 0);
    }

    /// <summary>One key of an assistant on this network in PadForge.xml
    /// (#600 item 53): its name, its level and the hash of its text.</summary>
    public sealed class NetworkKeyData
    {
        [XmlAttribute] public string Id { get; set; }
        [XmlAttribute] public string Name { get; set; }

        /// <summary>The level, as AutomationLevel's number.</summary>
        [XmlAttribute] public int Level { get; set; }

        /// <summary>SHA-256 of the key's text, lower hex. The key itself is
        /// never written.</summary>
        [XmlAttribute] public string Hash { get; set; }

        [XmlAttribute] public DateTime Made { get; set; }
        [XmlAttribute] public DateTime LastUsed { get; set; }

        /// <summary>Its reach (#600 DESIGN-2 N3), absent for all of
        /// PadForge.</summary>
        [XmlElement("Reach")] public ReachData Reach { get; set; }
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

        /// <summary>Its reach (#600 DESIGN-2 N3), absent for all of
        /// PadForge.</summary>
        [XmlElement("Reach")] public ReachData Reach { get; set; }
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
        private readonly List<NetworkKey> _keys = new();

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

        /// <summary>The ChatGPT tunnel's runtime key as its blob (#600
        /// DESIGN-2 N4), or null.</summary>
        public string RuntimeKeyBlob { get; private set; }

        /// <summary>The ChatGPT tunnel's ID, or null.</summary>
        public string TunnelId { get; private set; }

        public IReadOnlyList<WebGrant> Grants
        {
            get { lock (_sync) return _grants.ToList(); }
        }

        /// <summary>Reach PadForge From This Network's switch (#600 item 53).</summary>
        public bool NetworkOn { get; private set; }

        /// <summary>The network door's port, or 0 before it was first
        /// turned on.</summary>
        public int NetworkPort { get; private set; }

        public IReadOnlyList<NetworkKey> Keys
        {
            get { lock (_sync) return _keys.ToList(); }
        }

        internal void Load(WebAccessData data)
        {
            lock (_sync)
            {
                _grants.Clear();
                _keys.Clear();
                On = data?.On ?? false;
                NetworkOn = data?.Network ?? false;
                NetworkPort = data?.NetworkPort is int np && NetworkDoor.IsPort(np) ? np : 0;
                foreach (var k in data?.Keys ?? Array.Empty<NetworkKeyData>())
                {
                    if (string.IsNullOrEmpty(k?.Id) || string.IsNullOrEmpty(k.Name) || Hex(k.Hash) == null || _keys.Count >= NetworkDoor.MostKeys) continue;
                    _keys.Add(new NetworkKey
                    {
                        Id = k.Id, Name = k.Name, Hash = k.Hash,
                        Level = Enum.IsDefined(typeof(AutomationLevel), k.Level) ? (AutomationLevel)k.Level : AutomationLevel.Commands,
                        Made = Utc(k.Made), LastUsed = k.LastUsed == default ? null : Utc(k.LastUsed),
                        Reach = ProgramReach.FromData(k.Reach),
                    });
                }
                Tunnel = string.IsNullOrEmpty(data?.Tunnel) ? null : data.Tunnel;
                Address = string.IsNullOrEmpty(data?.Address) ? null : data.Address;
                Port = data?.Port is > 1024 and < 65536 ? data.Port : 0;
                RuntimeKeyBlob = string.IsNullOrEmpty(data?.RuntimeKey) ? null : data.RuntimeKey;
                TunnelId = TunnelClient.IsTunnelId(data?.TunnelId) ? data.TunnelId : null;
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
                        Reach = ProgramReach.FromData(a.Reach),
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
                    RuntimeKey = RuntimeKeyBlob,
                    TunnelId = TunnelId,
                    Assistants = _grants.Count == 0 ? null : _grants.Select(g => new WebAssistantData
                    {
                        Id = g.Id, Origin = g.Origin, Name = g.Name, ClientId = g.ClientId, Redirect = g.RedirectUri, Resource = g.Resource,
                        AccessHash = g.AccessHash, AccessUntil = g.AccessUntil.UtcDateTime,
                        RefreshHash = g.RefreshHash, RefreshUntil = g.RefreshUntil.UtcDateTime,
                        Allowed = g.Allowed.UtcDateTime, LastUsed = g.LastUsed?.UtcDateTime ?? default,
                        Reach = g.Reach?.ToData(),
                    }).ToArray(),
                    Network = NetworkOn,
                    NetworkPort = NetworkPort,
                    Keys = _keys.Count == 0 ? null : _keys.Select(k => new NetworkKeyData
                    {
                        Id = k.Id, Name = k.Name, Level = (int)k.Level, Hash = k.Hash,
                        Made = k.Made.UtcDateTime, LastUsed = k.LastUsed?.UtcDateTime ?? default,
                        Reach = k.Reach?.ToData(),
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

        /// <summary>Keeps the runtime key's blob, or forgets it with
        /// null.</summary>
        internal void SetRuntimeKey(string blob) => Change(() =>
        {
            blob = string.IsNullOrEmpty(blob) ? null : blob;
            if (RuntimeKeyBlob == blob) return false;
            RuntimeKeyBlob = blob;
            return true;
        });

        internal void SetTunnelId(string id) => Change(() =>
        {
            id = TunnelClient.IsTunnelId(id) ? id : null;
            if (TunnelId == id) return false;
            TunnelId = id;
            return true;
        });

        internal void SetNetwork(bool on) => Change(() => { if (NetworkOn == on) return false; NetworkOn = on; return true; });

        internal void SetNetworkPort(int port) => Change(() =>
        {
            if (NetworkPort == port || !NetworkDoor.IsPort(port)) return false;
            NetworkPort = port;
            return true;
        });

        /// <summary>Keeps a new network key, up to the most kept.</summary>
        internal bool AddKey(NetworkKey key)
        {
            bool added = false;
            Change(() =>
            {
                if (_keys.Count >= NetworkDoor.MostKeys || Hex(key?.Hash) == null) return false;
                _keys.Add(key);
                added = true;
                return true;
            });
            return added;
        }

        /// <summary>Sets a network key's reach (#600 DESIGN-2 N3).</summary>
        internal bool SetKeyReach(string id, ProgramReach reach)
        {
            bool changed = false;
            Change(() =>
            {
                int i = _keys.FindIndex(k => k.Id == id);
                if (i < 0 || Equals(_keys[i].Reach, reach)) return false;
                _keys[i] = _keys[i] with { Reach = reach };
                return changed = true;
            });
            return changed;
        }

        internal bool RevokeKey(string id)
        {
            bool removed = false;
            Change(() => removed = _keys.RemoveAll(k => k.Id == id) > 0);
            return removed;
        }

        internal void KeyUsed(string id, DateTimeOffset when)
            => Change(() =>
            {
                int i = _keys.FindIndex(k => k.Id == id);
                if (i < 0) return false;
                _keys[i] = _keys[i] with { LastUsed = when };
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
