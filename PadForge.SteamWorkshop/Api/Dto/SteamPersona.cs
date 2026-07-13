using System;
using System.Xml.Linq;

namespace PadForge.SteamWorkshop.Api.Dto
{
    /// <summary>
    /// A creator's public profile fields, parsed from the anonymous
    /// <c>steamcommunity.com/profiles/{id}?xml=1</c> document.
    /// </summary>
    public sealed class SteamPersona
    {
        public ulong SteamId64 { get; }

        public string PersonaName { get; }

        public string AvatarMediumUrl { get; }

        public string AvatarFullUrl { get; }

        public SteamPersona(ulong steamId64, string personaName, string avatarMediumUrl, string avatarFullUrl)
        {
            SteamId64 = steamId64;
            PersonaName = personaName;
            AvatarMediumUrl = avatarMediumUrl;
            AvatarFullUrl = avatarFullUrl;
        }

        /// <summary>
        /// Parses the profile XML. The persona name lives in <c>&lt;steamID&gt;</c> (often
        /// CDATA); avatar URLs in <c>&lt;avatarMedium&gt;</c> / <c>&lt;avatarFull&gt;</c>.
        /// Returns a persona with best-effort fields; never throws on missing elements.
        /// </summary>
        public static SteamPersona FromProfileXml(ulong steamId64, string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return new SteamPersona(steamId64, null, null, null);

            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml);
            }
            catch (System.Xml.XmlException)
            {
                return new SteamPersona(steamId64, null, null, null);
            }

            var root = doc.Root;
            if (root == null)
                return new SteamPersona(steamId64, null, null, null);

            var id64 = steamId64;
            var id64Text = (string)root.Element("steamID64");
            if (!string.IsNullOrEmpty(id64Text) && ulong.TryParse(id64Text, out var parsed))
                id64 = parsed;

            return new SteamPersona(
                id64,
                Trim((string)root.Element("steamID")),
                Trim((string)root.Element("avatarMedium")),
                Trim((string)root.Element("avatarFull")));
        }

        private static string Trim(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
