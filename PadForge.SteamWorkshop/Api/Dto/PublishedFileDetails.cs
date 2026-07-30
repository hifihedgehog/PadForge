using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace PadForge.SteamWorkshop.Api.Dto
{
    /// <summary>Envelope of <c>ISteamRemoteStorage/GetPublishedFileDetails/v1/</c>.</summary>
    public sealed class GetPublishedFileDetailsResponse
    {
        [JsonPropertyName("response")]
        public PublishedFileDetailsResult Response { get; set; }
    }

    public sealed class PublishedFileDetailsResult
    {
        [JsonPropertyName("result")]
        public int Result { get; set; }

        [JsonPropertyName("resultcount")]
        public int ResultCount { get; set; }

        [JsonPropertyName("publishedfiledetails")]
        public List<PublishedFileDetails> PublishedFileDetails { get; set; }
    }

    /// <summary>Per-file Workshop metadata (subset PadForge needs, plus overflow).</summary>
    public sealed class PublishedFileDetails
    {
        [JsonPropertyName("publishedfileid")]
        public string PublishedFileId { get; set; }

        /// <summary>Per-item result code (1 == OK). A missing/banned item returns a non-1 code.</summary>
        [JsonPropertyName("result")]
        public int Result { get; set; }

        [JsonPropertyName("creator")]
        public string Creator { get; set; }

        [JsonPropertyName("creator_app_id")]
        public int CreatorAppId { get; set; }

        [JsonPropertyName("consumer_app_id")]
        public int ConsumerAppId { get; set; }

        [JsonPropertyName("filename")]
        public string Filename { get; set; }

        /// <summary>File size in bytes, delivered as a string by Steam. See <see cref="FileSizeBytes"/>.</summary>
        [JsonPropertyName("file_size")]
        public string FileSize { get; set; }

        [JsonPropertyName("file_url")]
        public string FileUrl { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("time_created")]
        public long TimeCreated { get; set; }

        [JsonPropertyName("time_updated")]
        public long TimeUpdated { get; set; }

        [JsonPropertyName("visibility")]
        public int Visibility { get; set; }

        [JsonPropertyName("banned")]
        public int Banned { get; set; }

        [JsonPropertyName("subscriptions")]
        public long Subscriptions { get; set; }

        [JsonPropertyName("lifetime_subscriptions")]
        public long LifetimeSubscriptions { get; set; }

        [JsonPropertyName("views")]
        public long Views { get; set; }

        [JsonPropertyName("tags")]
        public List<PublishedFileTag> Tags { get; set; }

        [JsonExtensionData]
        public Dictionary<string, object> Extra { get; set; }

        /// <summary>The <c>file_size</c> string parsed to bytes, or null if absent/non-numeric.</summary>
        [JsonIgnore]
        public long? FileSizeBytes =>
            long.TryParse(FileSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (long?)null;

        /// <summary>True when this entry has no CDN <c>file_url</c> (a legacy, non-downloadable config).</summary>
        [JsonIgnore]
        public bool IsLegacyNonDownloadable => string.IsNullOrEmpty(FileUrl);
    }

    public sealed class PublishedFileTag
    {
        [JsonPropertyName("tag")]
        public string Tag { get; set; }
    }
}
