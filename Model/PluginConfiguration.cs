namespace ManageComingSoon.Model
{
    using MediaBrowser.Model.Plugins;

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
    }
}