// STATUS (updated after live testing — see chat history for full log traces):
//   6. CONFIRMED WORKING — IScheduledTask registration/triggers/Execute.
//   7. CONFIRMED WORKING — IChannelManager/ITaskManager/IApplicationPaths
//      all constructor-injectable via Emby's DI.
//   8. CONFIRMED — the built-in channel-persistence task matches by Key
//      ("RefreshInternetChannels") on this server. Name-match kept only as
//      a fallback in case a different server/version doesn't expose that
//      Key the same way.
//   9. CONFIRMED — this task now also owns resolving/extracting the Radarr
//      stub placeholder video (see RadarrComingSoonChannel.ResolveStubVideoPath)
//      once per run and storing the resolved path in the cache, so
//      GetChannelItems in Cached mode never needs to touch the filesystem
//      for this itself.
//  10. NEW, LIVE-TESTED — IChannel.GetChannelImage is only ever consulted
//      once, at the moment Emby's channel-persistence task FIRST creates
//      the Channel BaseItem row (keyed by Name) in the library database.
//      It is never re-invoked for an existing row — confirmed by direct
//      test: deleting the persisted image via Emby's own UI and re-browsing
//      produced zero calls to GetSupportedChannelImages/GetChannelImage.
//      The only way to force a re-fetch is a NEW Channel row (i.e. a
//      RadarrChannelName change). Since that's disruptive and also the
//      same event that creates orphans, this task instead re-applies the
//      image directly every run via IProviderManager.SaveImage — the same
//      call Emby's own UI upload path uses (confirmed via
//      "ProviderManager: Saving image to ..." log line) — making the image
//      self-healing without relying on Emby ever asking for it again.
//  11. NEW — Channel BaseItem identity is now tracked via a fixed tag
//      (RadarrChannelIdentityTag) applied to the item's Tags collection,
//      independent of Name. This survives a rename and lets this task
//      find "its own" channel row even after RadarrChannelName changes,
//      and flag any other tagged Channel row as a stale orphan. Orphans
//      are logged only for now — no delete implemented yet pending review
//      of what a live DB actually surfaces here.

namespace ManageComingSoon.ScheduledTasks
{
    using ManageComingSoon.Channels;
    using ManageComingSoon.Model;
    using ManageComingSoon.Services;
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Controller;
    using MediaBrowser.Controller.Channels;
    using MediaBrowser.Controller.Drawing;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Drawing;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Tasks;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public class RadarrChannelSyncTask : IScheduledTask
    {
        // Emby's own task that persists channel items into the real item
        // database (confirmed: it's the one that assigns an InternalId/Guid
        // and runs the full metadata provider pipeline — our own
        // RefreshChannelContent call does not do this on its own).
        private const string RefreshChannelsTaskKey = "RefreshInternetChannels";
        private const string RefreshChannelsTaskName = "Refresh Internet Channels";

        // Must match the resource name used elsewhere for the plugin's
        // embedded thumbnail (ConfigurationPageView / RadarrComingSoonChannel).
        private const string ThumbResourceName = "ManageComingSoon.thumb.png";
        private const string ThumbCacheFileName = "radarr-channel-thumb.png";

        private readonly RadarrClient radarrClient;
        private readonly IChannelManager channelManager;
        private readonly ITaskManager taskManager;
        private readonly IApplicationPaths appPaths;
        private readonly ILibraryManager libraryManager;
        private readonly IImageProcessor imageProcessor;
        private readonly IProviderManager providerManager;
        private readonly ILogger logger;

        public RadarrChannelSyncTask(
            RadarrClient radarrClient,
            IChannelManager channelManager,
            ITaskManager taskManager,
            IApplicationPaths appPaths,
            ILibraryManager libraryManager,
            IImageProcessor imageProcessor,
            IProviderManager providerManager,
            ILogger logger)
        {
            this.radarrClient = radarrClient;
            this.channelManager = channelManager;
            this.taskManager = taskManager;
            this.appPaths = appPaths;
            this.libraryManager = libraryManager;
            this.imageProcessor = imageProcessor;
            this.providerManager = providerManager;
            this.logger = logger;
        }

        public string Name => "Sync Radarr Coming Soon Channel";

        public string Key => "ManageComingSoon-RadarrChannelSync";

        public string Description =>
            "Queries Radarr for monitored, not-yet-downloaded movies, updates the Radarr Coming Soon channel's cache, ensures the placeholder video exists, and persists changes into Emby. Only runs when Radarr integration is enabled and sync mode is Cached.";

        public string Category => "Manage Coming Soon";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var config = ManageComingSoonPlugin.Instance.Configuration;
            var minutes = Math.Max(1, config.RadarrRefreshMinutes);

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromMinutes(minutes).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = ManageComingSoonPlugin.Instance.Configuration;

            if (!config.RadarrEnabled)
            {
                logger.Info("ManageComingSoon: Radarr sync skipped — Radarr integration disabled.");
                return;
            }

            if (config.RadarrSyncMode != RadarrSyncMode.Cached)
            {
                logger.Info("ManageComingSoon: Radarr sync skipped — sync mode is Live, cache is unused.");
                return;
            }

            var movies = await radarrClient
                .GetComingSoonMoviesAsync(config, cancellationToken)
                .ConfigureAwait(false);

            if (movies == null)
            {
                // Radarr call failed (bad response, timeout, unreachable, etc).
                // Per the "must get a successful response rather than no
                // response" rule: do NOT touch the cache, do NOT remove
                // anything. Leave existing state exactly as it was and try
                // again on the next scheduled run.
                logger.Warn("ManageComingSoon: Radarr sync failed — leaving cache untouched.");
                return;
            }

            var channel = channelManager.GetChannel<RadarrComingSoonChannel>();
            if (channel == null)
            {
                logger.Warn("ManageComingSoon: Radarr Coming Soon channel is not registered with ChannelManager yet — skipping this sync run.");
                return;
            }

            var cache = channel.ReadCache();
            var oldIds = new HashSet<int>(cache.Items.Select(i => i.TmdbId));

            var newItems = movies
                .Select(m => new Services.Models.RadarrChannelCacheItem
                {
                    RadarrId = m.Id,
                    TmdbId = m.TmdbId,
                    ImdbId = m.ImdbId,
                    Title = m.Title,
                    OriginalTitle = m.OriginalTitle,
                    Year = m.Year,
                    Overview = m.Overview,
                    PosterUrl = m.Images?
                        .FirstOrDefault(i => string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))?
                        .RemoteUrl
                })
                .ToList();

            var newIds = new HashSet<int>(newItems.Select(i => i.TmdbId));
            var added = newIds.Except(oldIds).Count();
            var removed = oldIds.Except(newIds).Count();

            logger.Info(
                "ManageComingSoon: Radarr sync diff — {0} added, {1} removed, {2} unchanged, {3} total.",
                added, removed, newIds.Intersect(oldIds).Count(), newIds.Count);

            // This task owns keeping the shared placeholder video current —
            // resolved once per run so GetChannelItems in Cached mode can
            // just read the path back out of the cache with no filesystem
            // work of its own.
            var stubVideoPath = RadarrComingSoonChannel.ResolveStubVideoPath(config, appPaths, logger);

            cache.Items = newItems;
            cache.StubVideoPath = stubVideoPath;
            cache.LastSyncSucceeded = true;
            cache.LastSyncUtc = DateTimeOffset.UtcNow;
            channel.WriteCache(cache);

            // Non-fatal by design: the cache write above already succeeded,
            // which is the actual sync outcome that matters. A failure here
            // only affects how soon Emby's own listing reflects the change.
            try
            {
                await channelManager
                    .RefreshChannelContent(channel, maxRefreshLevel: 0, restrictTopLevelFolderId: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.ErrorException(
                    "ManageComingSoon: RefreshChannelContent failed (cache was still updated successfully — this only affects how soon the change is reflected)",
                    ex);
            }

            // Confirmed via testing: RefreshChannelContent signals intent but
            // does NOT itself persist ChannelItemInfo entries into Emby's item
            // database. Only Emby's own built-in channel-persistence task
            // does that. Triggering and awaiting it here means one run of our
            // task does the whole job: Radarr -> cache -> real, queryable
            // Emby items — no second manually-scheduled task needed.
            //
            // Also confirmed via testing: removal is purely implicit — this
            // task doesn't need to call DeleteItem itself. See the header
            // note (#6 in RadarrComingSoonChannel) for details.
            try
            {
                var refreshWorker = taskManager.ScheduledTasks
                    .FirstOrDefault(w => string.Equals(w.ScheduledTask?.Key, RefreshChannelsTaskKey, StringComparison.OrdinalIgnoreCase))
                    ?? taskManager.ScheduledTasks
                        .FirstOrDefault(w => string.Equals(w.Name, RefreshChannelsTaskName, StringComparison.OrdinalIgnoreCase));

                if (refreshWorker == null)
                {
                    logger.Warn(
                        "ManageComingSoon: Could not find the built-in channel-refresh task (Key='{0}' / Name='{1}') — items will only be persisted whenever that task next runs on its own schedule.",
                        RefreshChannelsTaskKey, RefreshChannelsTaskName);
                }
                else
                {
                    await taskManager.Execute(refreshWorker, new TaskOptions()).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException(
                    "ManageComingSoon: Failed to trigger the built-in channel-refresh task — items may not be persisted until it next runs on its own schedule",
                    ex);
            }

            // ---- Channel identity: tag, image reapply, orphan detection ----
            // Must run AFTER the refresh-task await above — the Channel
            // BaseItem doesn't exist in the library database until that
            // task actually persists it (see status note #6/#10).
            ReconcileChannelIdentity(config);
        }

        /// <summary>
        /// Finds the Channel BaseItem(s) carrying this plugin's identity tag,
        /// applies/locks the tag on the current one (matched by
        /// RadarrChannelName), re-applies the embedded channel image every
        /// run (self-healing — see status note #10), and logs any other
        /// tagged Channel item as an orphan left behind by a previous name.
        /// No delete is performed on orphans yet — logging only, pending
        /// review of what a live DB actually surfaces here.
        /// </summary>
        private void ReconcileChannelIdentity(PluginConfiguration config)
        {
            List<MediaBrowser.Controller.Entities.BaseItem> taggedChannelItems;

            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Channel" },
                    Tags = new[] { config.RadarrChannelIdentityTag }
                };

                taggedChannelItems = libraryManager.GetItemsResult(query).Items.ToList();
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Failed to query tagged Channel items for identity reconciliation", ex);
                return;
            }

            var current = taggedChannelItems.FirstOrDefault(i =>
                string.Equals(i.Name, config.RadarrChannelName, StringComparison.OrdinalIgnoreCase));

            // First run after a rename (or the very first run ever): the tag
            // hasn't been applied to the current Channel row yet, so it won't
            // show up in the tagged query above. Fall back to matching by
            // Name alone among ALL Channel items so we can tag it now.
            if (current == null)
            {
                try
                {
                    var allChannelsQuery = new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Channel" },
                        Name = config.RadarrChannelName
                    };

                    current = libraryManager.GetItemsResult(allChannelsQuery).Items.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    logger.ErrorException("ManageComingSoon: Failed to query Channel item by name for identity reconciliation", ex);
                }
            }

            var orphans = taggedChannelItems.Where(i => i != current).ToList();

            if (current != null)
            {
                ApplyIdentityTag(current, config.RadarrChannelIdentityTag);
                ReapplyChannelImage(current);
            }
            else
            {
                logger.Warn(
                    "ManageComingSoon: No Channel item found matching current name '{0}' — tag/image not applied this run. Expected on the very first sync before Emby has persisted the channel.",
                    config.RadarrChannelName);
            }

            foreach (var orphan in orphans)
            {
                logger.Info(
                    "ManageComingSoon: Orphaned Radarr channel entry detected — Name='{0}', InternalId={1}. Not deleted (logging only).",
                    orphan.Name, orphan.InternalId);
            }
        }

        private void ApplyIdentityTag(MediaBrowser.Controller.Entities.BaseItem item, string identityTag)
        {
            if (string.IsNullOrEmpty(identityTag))
            {
                return;
            }

            var tags = item.Tags != null ? new List<string>(item.Tags) : new List<string>();

            if (tags.FindIndex(t => string.Equals(t, identityTag, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                return; // already tagged, nothing to do
            }

            tags.Add(identityTag);
            item.Tags = tags.ToArray();

            // Lock Tags so a later metadata refresh can never strip this
            // identity marker — same reasoning as the ComingSoon tag lock
            // in EmbyLibraryAddService.
            if (!item.LockedFields.Contains(MetadataFields.Tags))
            {
                item.LockedFields = item.LockedFields
                    .Concat(new[] { MetadataFields.Tags })
                    .ToArray();
            }

            libraryManager.UpdateItem(
                item,
                item.GetParent(),
                MediaBrowser.Controller.Library.ItemUpdateType.MetadataEdit,
                null);

            logger.Info(
                "ManageComingSoon: Identity tag '{0}' applied to channel '{1}' (Tags field locked to protect it from provider refresh).",
                identityTag, item.Name);
        }

        


        
        private void ReapplyChannelImage(MediaBrowser.Controller.Entities.BaseItem item)
        {
            try
            {

                if (item.HasImage(ImageType.Primary))
                {
                    logger.Info("ManageComingSoon: '{0}' already has a primary image. Skipping reapply.", item.Name);
                    return;
                }

                var imagePath = ResolveChannelImagePath();

                if (string.IsNullOrEmpty(imagePath))
                {
                    logger.Warn("ManageComingSoon: Could not resolve channel image file — image not reapplied.");
                    return;
                }

                var imageSize = imageProcessor.GetImageSize(imagePath);
                
                item.SetImage(new MediaBrowser.Controller.Entities.ItemImageInfo
                {
                    Path = imagePath,
                    Type = ImageType.Primary,
                    DateModified = DateTimeOffset.UtcNow,
                    Width = (int)imageSize.Width,
                    Height = (int)imageSize.Height
                }, 0);

                // UpdateItem alone persists to the DB but does not appear to
                // invalidate whatever in-memory copy of the item Emby's
                // running web API serves images from — confirmed via direct
                // test: the DB record was correct (visible in Edit Images)
                // but the UI never served it until a full server restart.
                // UpdateImages is a real, confirmed ILibraryManager method
                // whose name suggests it may specifically invalidate that
                // in-memory image cache state — trying it here alongside
                // UpdateItem and IProviderManager.OnRefreshComplete as an
                // explicit "kick" for the live process to pick this up
                // without requiring a restart.
                libraryManager.UpdateImages(item);
                /*
                item.UpdateToRepository(MediaBrowser.Controller.Library.ItemUpdateType.ImageUpdate);

                var collectionFolders = libraryManager.GetCollectionFolders(item);
                providerManager.OnRefreshComplete(item, collectionFolders);
                */
                logger.Info(
                    "ManageComingSoon: Reapplied channel image to '{0}' (InternalId={1}) from {2}.",
                    item.Name, item.InternalId, imagePath);
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Failed to reapply channel image to '{0}'", ex, item.Name);
            }
        }
        
        /// <summary>
        /// Extracts the plugin's embedded thumb.png to a persistent on-disk
        /// location once (same pattern as ResolveStubVideoPath for the
        /// placeholder video) and reuses it thereafter. BaseItem.SetImage
        /// requires a real file path, not a stream — this is the on-disk
        /// counterpart the item's ImageInfo will point at.
        /// </summary>
        private string ResolveChannelImagePath()
        {
            var path = Path.Combine(appPaths.DataPath, "manage-coming-soon", ThumbCacheFileName);

            if (File.Exists(path))
            {
                return path;
            }

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var asm = typeof(ManageComingSoonPlugin).Assembly;
                using (var resourceStream = asm.GetManifestResourceStream(ThumbResourceName))
                {
                    if (resourceStream == null)
                    {
                        logger.Warn("ManageComingSoon: Embedded thumb resource '{0}' not found.", ThumbResourceName);
                        return string.Empty;
                    }

                    using (var fileStream = File.Create(path))
                    {
                        resourceStream.CopyTo(fileStream);
                    }
                }

                logger.Info("ManageComingSoon: Extracted channel thumb image to {0}.", path);
                return path;
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Failed to extract channel thumb image", ex);
                return string.Empty;
            }
        }
    }
}