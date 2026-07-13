using System.Collections.Generic;

namespace ManageComingSoon.Services.Models
{
    /// <summary>
    /// One channel item's worth of state, as last successfully synced from
    /// Radarr. Written by the scheduled task in Cached mode; read by the
    /// channel's GetChannelItems. Deliberately flat/small — enough to
    /// rebuild a ChannelItemInfo without re-hitting Radarr or TMDB.
    /// </summary>
    public class RadarrChannelCacheItem
    {
        public int RadarrId { get; set; }
        public int TmdbId { get; set; }
        public string ImdbId { get; set; }
        public string Title { get; set; }
        public string OriginalTitle { get; set; }
        public int Year { get; set; }
        public string Overview { get; set; }
        public string PosterUrl { get; set; }
    }

    /// <summary>
    /// The full cache file contents. LastSyncSucceeded/LastSyncUtc are there
    /// so the channel (and any future UI) can distinguish "empty because
    /// nothing qualifies" from "empty because we've never had a successful
    /// sync yet" — both look like an empty Items list otherwise.
    /// </summary>
    public class RadarrChannelCache
    {
        public List<RadarrChannelCacheItem> Items { get; set; } = new List<RadarrChannelCacheItem>();
        public bool LastSyncSucceeded { get; set; }
        public System.DateTimeOffset? LastSyncUtc { get; set; }

        // Resolved, on-disk path to the placeholder video every channel
        // item's MediaSources points at for playback. Owned/updated by
        // RadarrChannelSyncTask each run (see ResolveStubVideoPath on
        // RadarrComingSoonChannel) — the channel itself only reads this in
        // Cached mode, avoiding a filesystem check on every browse request.
        public string StubVideoPath { get; set; } = string.Empty;
    }
}