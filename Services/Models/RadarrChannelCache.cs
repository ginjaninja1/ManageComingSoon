using System.Collections.Generic;

namespace ManageComingSoon.Services.Models
{
    public class RadarrChannelCacheItem
    {
        public int RadarrId { get; set; }
        public int TmdbId { get; set; }
        public string ImdbId { get; set; }
        public string TitleSlug { get; set; }
        public string Title { get; set; }
        public string OriginalTitle { get; set; }
        public int Year { get; set; }
        public string Overview { get; set; }
        public string PosterUrl { get; set; }
    }

    public class RadarrChannelCache
    {
        public List<RadarrChannelCacheItem> Items { get; set; } = new List<RadarrChannelCacheItem>();
        public bool LastSyncSucceeded { get; set; }
        public System.DateTimeOffset? LastSyncUtc { get; set; }
        public string StubVideoPath { get; set; } = string.Empty;
    }
}