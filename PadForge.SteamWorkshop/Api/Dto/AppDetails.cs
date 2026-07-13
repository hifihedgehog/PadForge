using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PadForge.SteamWorkshop.Api.Dto
{
    /// <summary>
    /// Per-app envelope from <c>store.steampowered.com/api/appdetails</c>. The top-level
    /// response is a map of app-id string to one of these.
    /// </summary>
    public sealed class AppDetailsEnvelope
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public AppDetailsData Data { get; set; }

        [JsonExtensionData]
        public Dictionary<string, object> Extra { get; set; }
    }

    /// <summary>The <c>data</c> payload of an app-details envelope (subset PadForge needs).</summary>
    public sealed class AppDetailsData
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("steam_appid")]
        public int SteamAppId { get; set; }

        [JsonPropertyName("controller_support")]
        public string ControllerSupport { get; set; }

        [JsonPropertyName("header_image")]
        public string HeaderImage { get; set; }

        [JsonPropertyName("capsule_image")]
        public string CapsuleImage { get; set; }

        [JsonExtensionData]
        public Dictionary<string, object> Extra { get; set; }
    }
}
