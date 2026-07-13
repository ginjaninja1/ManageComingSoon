namespace ManageComingSoon.Model
{
    using MediaBrowser.Model.Plugins;

    public enum RadarrSyncMode
    {
        // Scheduled task is the only thing that talks to Radarr; results are
        // written to a local cache file and GetChannelItems reads the cache
        // only. Default — decouples channel browsing from Radarr uptime.
        Cached,

        // GetChannelItems calls Radarr directly on every request. Simpler,
        // always fresh, but ties the channel's availability/responsiveness
        // to Radarr being reachable at that exact moment.
        Live
    }

    public enum RadarrRemovalStrategy
    {
        // Simply omit the item from the list returned by GetChannelItems /
        // the cache, then call RefreshChannelContent, relying on Emby to
        // reconcile and drop the stale entry on its own.
        Implicit,

        // Explicitly call ISupportsDelete.DeleteItem for anything that
        // fell out of scope, in addition to omitting it going forward.
        ExplicitDelete
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        // ---- TMDB ----
        public string TmdbApiKey { get; set; } = string.Empty;

        // ---- Coming Soon target: stored as "LibraryName|Path" composite key ----
        public string ComingSoonTargetKey { get; set; } = string.Empty;

        // ---- Coming Soon placeholder video ----
        // No separate enable flag — an empty path means "use plugin default",
        // any non-empty path is treated as the active custom stub.
        public string ComingSoonStubVideoPath { get; set; } = string.Empty;

        // ---- Make Live target: stored as "LibraryName|Path" composite key ----
        public string MakeLiveTargetKey { get; set; } = string.Empty;

        // ---- Make Live options ----
        public bool MakeLiveMoveToNewLocation { get; set; } = false;

        public bool MakeLiveDeleteStubFile { get; set; } = true;

        // Only meaningful when MakeLiveDeleteStubFile is true — a stub file
        // larger than this (MB) is left in place rather than deleted, as a
        // safety net against deleting something that isn't actually a
        // placeholder. Checked as a per-item preflight in the Make Live
        // pipeline, alongside disk space and path-availability checks.
        public int MakeLiveDeleteStubFileMaxFileSize { get; set; } = 100;

        // Reverts the Tags lock instantiated by ComingSoonEntryPoint when the
        // placeholder was first ingested, once the movie is made live, so Tags
        // can be edited normally afterwards in the Metadata Editor. Has no
        // effect when MakeLiveDeleteStubFile is true — see Stage_UnlockTags's
        // doc comment in EmbyLibraryMakeService for why that combination is a
        // deliberate no-op rather than an oversight.
        public bool UnlockTags { get; set; } = true;

        // ---- Coming Soon tag ----
        // The tag text is always required (enforced to at least one character by
        // ConfigurationPageView on save). The tag is the tracker's single source
        // of truth — removing the ability to disable it entirely keeps the Make
        // Live state machine's page-navigation guarantees unconditional.
        public string ComingSoonTagText { get; set; } = "Coming Soon";

        public string EmbyApiKey { get; set; } = string.Empty;

        public string RadarrUrl { get; set; } = "http://127.0.0.1:7878";

        public string RadarrApiKey { get; set; } = "";

        public int RadarrRefreshMinutes { get; set; } = 15;

        // ---- Radarr "Coming Soon" channel ----
        // Master switch for the whole Radarr channel feature. Everything
        // below is inert while this is false.
        public bool RadarrEnabled { get; set; } = false;

        // Gates whether the sync is permitted to remove channel items at
        // all, independent of RadarrSyncMode/RadarrRemovalStrategy. This is
        // deliberately a separate setting from any other "enable delete"
        // flag elsewhere in the plugin (e.g. MakeLiveDeleteStubFile) — the
        // two are unrelated: one is a filesystem stub delete, this one is a
        // channel-item removal with no filesystem interaction at all.
        public bool RadarrEnableDelete { get; set; } = false;

        // Cached vs Live — see RadarrSyncMode doc comments above. Defaulting
        // to Cached because it's the safer, better-understood starting mode;
        // both are wired up so the two can be compared directly.
        public RadarrSyncMode RadarrSyncMode { get; set; } = RadarrSyncMode.Cached;

        // Implicit removal is confirmed sufficient on its own (Emby's built-in
        // channel-refresh reconciles purely from what GetChannelItems returns
        // each run) — this flag is kept as a safety toggle for the
        // user-initiated delete path (ISupportsDelete/CanDelete), not because
        // it's required for normal add/remove sync to work.
        public RadarrRemovalStrategy RadarrRemovalStrategy { get; set; } = RadarrRemovalStrategy.Implicit;

        // Same semantics as ComingSoonStubVideoPath: empty = use the plugin's
        // embedded default placeholder video; non-empty = a validated custom
        // path. This is the file every Radarr channel item's MediaSources
        // points at for playback, since channel items have no folder of
        // their own to hold a per-item stub copy.
        public string RadarrStubVideoPath { get; set; } = string.Empty;
    }
}