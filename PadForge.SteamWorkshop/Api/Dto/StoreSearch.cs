using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PadForge.SteamWorkshop.Api.Dto
{
    /// <summary>Response of <c>store.steampowered.com/api/storesearch</c>.</summary>
    public sealed class StoreSearchResponse
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("items")]
        public List<StoreSearchItem> Items { get; set; }

        [JsonExtensionData]
        public Dictionary<string, object> Extra { get; set; }
    }

    /// <summary>A single game returned by store search.</summary>
    public sealed class StoreSearchItem
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("tiny_image")]
        public string TinyImage { get; set; }

        [JsonPropertyName("controller_support")]
        public string ControllerSupport { get; set; }

        [JsonExtensionData]
        public Dictionary<string, object> Extra { get; set; }
    }
}
