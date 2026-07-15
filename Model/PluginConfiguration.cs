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

        // Display name of the Radarr channel in Emby. Read by
        // RadarrComingSoonChannel.Name. Changing this creates a new Channel
        // DB row in Emby (channels are keyed by Name) and orphans the old
        // one — see Project.md roadmap item 2 for the orphan-cleanup design,
        // not yet implemented pending a probe.
        public string RadarrChannelName { get; set; } = "Radarr Coming Soon";

        // Gates whether the sync is permitted to remove channel items at
        // all, independent of RadarrSyncMode/RadarrRemovalStrategy. Kept on
        // config for the ISupportsDelete/CanDelete code path (user-initiated
        // deletes from Emby's own UI) even though it is deliberately no
        // longer exposed in ConfigurationUI — confirmed not needed for
        // normal add/remove sync, which relies purely on implicit
        // reconciliation. See Project.md roadmap item 7.
        public bool RadarrEnableDelete { get; set; } = false;

        // Cached vs Live — see RadarrSyncMode doc comments above. Defaulting
        // to Cached because it's the safer, better-understood starting mode;
        // both are wired up so the two can be compared directly.
        public RadarrSyncMode RadarrSyncMode { get; set; } = RadarrSyncMode.Cached;

        // Retained on config for the same reason as RadarrEnableDelete above
        // (ISupportsDelete safety toggle) but no longer exposed in
        // ConfigurationUI — implicit removal alone is confirmed sufficient
        // for normal sync. See Project.md roadmap item 7.
        public RadarrRemovalStrategy RadarrRemovalStrategy { get; set; } = RadarrRemovalStrategy.Implicit;

        // Same semantics as ComingSoonStubVideoPath: empty = use the plugin's
        // embedded default placeholder video; non-empty = a validated custom
        // path. This is the file every Radarr channel item's MediaSources
        // points at for playback, since channel items have no folder of
        // their own to hold a per-item stub copy.
        public string RadarrStubVideoPath { get; set; } = string.Empty;

        // Fixed identity marker applied to the Channel BaseItem, independent of
        // RadarrChannelName. Survives a channel rename, letting the sync task
        // find "this plugin's channel" even after the Name-keyed DB row changes —
        // and flag any other Channel item carrying this tag as a stale orphan.
        public string RadarrChannelIdentityTag { get; set; } = "ManageComingSoon:RadarrChannel";

        // Internal bookkeeping — the identity tag value most recently written to
        // the Channel BaseItem by the reconciler. Not user-facing. Lets
        // ApplyIdentityTag know exactly which stale tag to remove when
        // RadarrChannelIdentityTag changes, instead of only ever adding and
        // leaving old/fragment values behind.
        public string RadarrChannelIdentityTagLastApplied { get; set; } = string.Empty;
    }
}