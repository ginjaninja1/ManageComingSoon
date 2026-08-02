// ManageComingSoon - IServerEntryPoint
// Hooks LibraryManager.ItemAdded to apply the "Coming Soon" tag AFTER Emby
// has fully ingested and committed the item's metadata to the database.
// This is the correct pattern — tagging inline during a scan risks being
// overwritten by Emby's own post-ingest metadata pass.
//
// Also notifies AddTitleTracker (via NotifyPathConfirmed) when an Add Coming
// Soon ingest completes. This is a signal only — it does NOT mutate the
// entry's State. AddTitleTracker.NotifyPathConfirmed doc comment explains
// why: AddTitleTask is the sole writer of terminal transitions (SetAdded/
// SetAddFailed), so this class never has direct write access to State. It
// races against AddTitleTask's own pipeline and applies completion itself,
// whichever confirms first.

namespace ManageComingSoon
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Persistence;
    using MediaBrowser.Controller.Plugins;
    using MediaBrowser.Model.Logging;
    using ManageComingSoon.Storage;   // Added
    using ManageComingSoon.Services;  // Added
    using ManageComingSoon.Model;
    using MediaBrowser.Common; // Added for IApplicationHost

    public class ComingSoonEntryPoint : IServerEntryPoint
    {
        // Tag text is read from config on every use so a change on the
        // Configuration tab takes effect on the next ItemAdded event without
        // a server restart.
        private static string ActiveTagText
        {
            get
            {
                var cfg = ManageComingSoonPlugin.Instance?.Configuration;
                if (cfg == null || string.IsNullOrEmpty(cfg.ComingSoonTagText))
                    return "Coming Soon";
                return cfg.ComingSoonTagText;
            }
        }

        private readonly ILibraryManager libraryManager;
        private readonly IItemRepository itemRepository;
        private readonly ILogger logger;
        private readonly IApplicationHost appHost; // Added

        // Paths registered by AddComingSoonAsync, consumed by ItemAdded handler.
        // Static so EmbyLibraryService can register without a reference to this instance.
        private static readonly Dictionary<string, ComingSoonMediaType> PendingPaths =
            new Dictionary<string, ComingSoonMediaType>(StringComparer.OrdinalIgnoreCase);
        private static readonly object PendingLock = new object();

        public ComingSoonEntryPoint(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            ILogManager logManager,
            IApplicationHost appHost)
        {
            this.libraryManager = libraryManager;
            this.itemRepository = itemRepository;
            this.logger = logManager.GetLogger("Manage Coming Soon");
            this.appHost = appHost;
        }

        // -----------------------------------------------------------------------
        // IServerEntryPoint
        // -----------------------------------------------------------------------

        public void Run()
        {
            try
            {
                this.logger.Info("ManageComingSoon: Initializing persistence stores on startup...");

                // 1. Initialize AddTitle Store & Tracker
                var addTitleStore = new AddTitleStore(this.appHost, this.logger);
                AddTitleTracker.Initialise(addTitleStore);

                // 2. Initialize MakeLive Store & Tracker
                var makeLiveStore = new MakeLiveStore(this.appHost, this.logger);
                MakeLiveTracker.Initialise(makeLiveStore);

                // 3. Process lifecycle hygiene exactly once on server boot
                MakeLiveTracker.PrepareForPageLoad();
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("ManageComingSoon: Critical failure initializing stores on startup", ex);
            }

            this.libraryManager.ItemAdded += OnItemAdded;
            this.logger.Info("ManageComingSoon: EntryPoint running – watching for Coming Soon items");
        }

        public void Dispose()
        {
            this.logger.Info("ManageComingSoon: ComingSoonEntryPoint.Dispose() starting");

            this.libraryManager.ItemAdded -= OnItemAdded;

            // Signal trackers to stop persisting during server shutdown
            AddTitleTracker.Shutdown();
            MakeLiveTracker.Shutdown(); // Cleanly added shutdown hook

            var active = MakeLiveTracker.GetActive();
            foreach (var entry in active)
            {
                this.logger.Warn(
                    "ManageComingSoon: Server shutting down with make-live in progress for '{0}' " +
                    "(state={1}, path={2}). The file move may be incomplete.",
                    entry.ItemName, entry.State, entry.FolderPath);
            }

            this.logger.Info("ManageComingSoon: ComingSoonEntryPoint.Dispose() complete");
        }

        // -----------------------------------------------------------------------
        // Public static: called by EmbyLibraryService to register a pending path
        // 
        // -----------------------------------------------------------------------

        public static void RegisterPendingPath(
            string folderPath, ComingSoonMediaType mediaType = ComingSoonMediaType.Movie)
        {
            lock (PendingLock)
                PendingPaths[folderPath] = mediaType;
        }

        // -----------------------------------------------------------------------
        // ItemAdded handler
        // -----------------------------------------------------------------------

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                var item = e.Item;
                if (item == null || string.IsNullOrEmpty(item.Path))
                    return;

                bool isMovie = item is MediaBrowser.Controller.Entities.Movies.Movie;
                bool isEpisode = string.Equals(item.GetType().Name, "Episode", StringComparison.Ordinal);
                bool isSeries = string.Equals(item.GetType().Name, "Series", StringComparison.Ordinal);
                if (!isMovie && !isEpisode && !isSeries)
                    return;

                // ---------------------------------------------------------------
                // Check 1: Is this a make-live completion?
                // ---------------------------------------------------------------
                var makeLiveEntry = ManageComingSoon.Services.MakeLiveTracker
                    .FindScanPendingByTargetPath(item.Path);

                if (makeLiveEntry != null)
                {
                    this.logger.Info(
                        "ManageComingSoon: ItemAdded for '{0}' matches make-live target – marking complete",
                        item.Name);
                    ManageComingSoon.Services.MakeLiveTracker.SetComplete(makeLiveEntry.FolderPath);
                    return;
                }

                // ---------------------------------------------------------------
                // Check 2: Is this a new Coming Soon placeholder to tag?
                // ---------------------------------------------------------------
                string matchedPath = null;
                ComingSoonMediaType matchedMediaType = ComingSoonMediaType.Movie;
                lock (PendingLock)
                {
                    foreach (var pending in PendingPaths)
                    {
                        if (item.Path.StartsWith(pending.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedPath = pending.Key;
                            matchedMediaType = pending.Value;
                            break;
                        }
                    }
                }

                if (matchedPath == null)
                    return;

                BaseItem tagTarget = item;
                if (matchedMediaType == ComingSoonMediaType.TvShow)
                {
                    while (tagTarget != null &&
                        !string.Equals(tagTarget.GetType().Name, "Series", StringComparison.Ordinal))
                        tagTarget = tagTarget.GetParent();
                    if (tagTarget == null) return;
                }

                this.logger.Info(
                    "ManageComingSoon: ItemAdded fired for '{0}' (path={1}) – applying Coming Soon tag",
                    tagTarget.Name, tagTarget.Path);

                // Apply tag using the pattern confirmed working by community:
                // item.Tags = array + UpdateItem(item, parent, type, null)
                string comingSoonTag = ActiveTagText;
                var tags = tagTarget.Tags != null ? new List<string>(tagTarget.Tags) : new List<string>();
                if (tags.FindIndex(t => string.Equals(t, comingSoonTag, StringComparison.OrdinalIgnoreCase)) < 0)
                {
                    tags.Add(comingSoonTag);
                    tagTarget.Tags = tags.ToArray();

                    // Lock Tags so a later metadata refresh — ours, a scheduled
                    // library scan, or a manual "Refresh Metadata" click in Emby's
                    // own UI — can never overwrite it and silently strip the tag.
                    // Confirmed necessary: a same-named external subtitle dropped
                    // into a Coming Soon folder makes Emby's own refresh pipeline
                    // treat it as a "subtitle track change" and re-run identity
                    // providers (FFProbe/MovieDb/Tvdb) against the item; without
                    // this lock, that provider re-run resets Tags to empty.
                    if (!tagTarget.LockedFields.Contains(MediaBrowser.Model.Entities.MetadataFields.Tags))
                    {
                        tagTarget.LockedFields = tagTarget.LockedFields
                            .Concat(new[] { MediaBrowser.Model.Entities.MetadataFields.Tags })
                            .ToArray();
                    }

                    this.libraryManager.UpdateItem(
                        tagTarget,
                        tagTarget.GetParent(),
                        ItemUpdateType.MetadataEdit,
                        null);

                    this.logger.Info(
                        "ManageComingSoon: Tag '{0}' applied to '{1}' (Tags field locked to protect it from provider refresh)",
                        comingSoonTag, tagTarget.Name);
                }

                // ---------------------------------------------------------------
                // Check 3: Notify AddTitleTracker that Emby confirmed this path.
                // This is a signal only — it does NOT mark the entry Added.
                // AddTitleTask remains the sole writer of terminal transitions;
                // it watches this signal via a fast-path race against its own
                // pipeline and applies SetAdded itself, whichever wins. This is
                // independent of the tag application above — both always run.
                // ---------------------------------------------------------------
                var addEntry = matchedMediaType == ComingSoonMediaType.Movie
                    ? ManageComingSoon.Services.AddTitleTracker.FindAddingByFolderPath(matchedPath)
                    : null;

                if (addEntry != null)
                {
                    this.logger.Info(
                        "ManageComingSoon: ItemAdded for '{0}' matches AddTitleTracker entry '{1}' – notifying AddTitleTask",
                        item.Name, addEntry.Id);
                    ManageComingSoon.Services.AddTitleTracker.NotifyPathConfirmed(matchedPath);
                }

                // Remove from pending — successfully handled
                lock (PendingLock)
                    PendingPaths.Remove(matchedPath);
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("ManageComingSoon: Error in OnItemAdded", ex);
            }
        }
    }
}
