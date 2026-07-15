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
        private const string RefreshChannelsTaskKey = "RefreshInternetChannels";
        private const string RefreshChannelsTaskName = "Refresh Internet Channels";

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

            var newItems = movies
                .Select(m => new Services.Models.RadarrChannelCacheItem
                {
                    RadarrId = m.Id,
                    TmdbId = m.TmdbId,
                    ImdbId = m.ImdbId,
                    TitleSlug = m.TitleSlug,
                    Title = m.Title,
                    OriginalTitle = m.OriginalTitle,
                    Year = m.Year,
                    Overview = m.Overview,
                    PosterUrl = m.Images?
                        .FirstOrDefault(i => string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))?
                        .RemoteUrl
                })
                .ToList();

            var oldSlugs = new HashSet<string>(cache.Items.Select(i => i.TitleSlug));
            var newSlugs = new HashSet<string>(newItems.Select(i => i.TitleSlug));

            var addedSlugs = newSlugs.Except(oldSlugs).ToList();
            var removedSlugs = oldSlugs.Except(newSlugs).ToList();

            logger.Info(
                "ManageComingSoon: Radarr sync diff — {0} added, {1} removed, {2} unchanged, {3} total.",
                addedSlugs.Count, removedSlugs.Count, newSlugs.Intersect(oldSlugs).Count(), newSlugs.Count);

            foreach (var item in newItems)
            {
                logger.Debug(
                    "ManageComingSoon: Sync item — TitleSlug='{0}', TmdbId={1}, Title='{2}'.",
                    item.TitleSlug, item.TmdbId, item.Title);
            }

            var stubVideoPath = RadarrComingSoonChannel.ResolveStubVideoPath(config, appPaths, logger);

            cache.Items = newItems;
            cache.StubVideoPath = stubVideoPath;
            cache.LastSyncSucceeded = true;
            cache.LastSyncUtc = DateTimeOffset.UtcNow;
            channel.WriteCache(cache);

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

            ReconcileChannelIdentity(config);
        }

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
                try
                {
                    channelManager.DeleteItem(orphan).GetAwaiter().GetResult();

                    logger.Info(
                        "ManageComingSoon: Orphaned Radarr channel entry deleted — Name='{0}', InternalId={1}.",
                        orphan.Name, orphan.InternalId);
                }
                catch (Exception ex)
                {
                    logger.ErrorException(
                        "ManageComingSoon: Failed to delete orphaned Radarr channel entry — Name='{0}', InternalId={1}.",
                        ex, orphan.Name, orphan.InternalId);
                }
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
                return;
            }

            tags.Add(identityTag);
            item.Tags = tags.ToArray();

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

                libraryManager.UpdateImages(item);

                logger.Info(
                    "ManageComingSoon: Reapplied channel image to '{0}' (InternalId={1}) from {2}.",
                    item.Name, item.InternalId, imagePath);
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Failed to reapply channel image to '{0}'", ex, item.Name);
            }
        }

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