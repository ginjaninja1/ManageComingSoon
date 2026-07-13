// STATUS (updated after live testing — see chat history for full log traces):
//   6. CONFIRMED WORKING — IScheduledTask registration/triggers/Execute.
//   7. CONFIRMED WORKING — IChannelManager/ITaskManager/IApplicationPaths
//      all constructor-injectable via Emby's DI.
//   8. CONFIRMED — the built-in channel-persistence task matches by Key
//      ("RefreshInternetChannels") on this server. Name-match kept only as
//      a fallback in case a different server/version doesn't expose that
//      Key the same way.
//   9. NEW — this task now also owns resolving/extracting the Radarr stub
//      placeholder video (see RadarrComingSoonChannel.ResolveStubVideoPath)
//      once per run and storing the resolved path in the cache, so
//      GetChannelItems in Cached mode never needs to touch the filesystem
//      for this itself.

namespace ManageComingSoon.ScheduledTasks
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using ManageComingSoon.Channels;
    using ManageComingSoon.Model;
    using ManageComingSoon.Services;
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Controller;
    using MediaBrowser.Controller.Channels;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Tasks;

    public class RadarrChannelSyncTask : IScheduledTask
    {
        // Emby's own task that persists channel items into the real item
        // database (confirmed: it's the one that assigns an InternalId/Guid
        // and runs the full metadata provider pipeline — our own
        // RefreshChannelContent call does not do this on its own).
        private const string RefreshChannelsTaskKey = "RefreshInternetChannels";
        private const string RefreshChannelsTaskName = "Refresh Internet Channels";

        private readonly RadarrClient radarrClient;
        private readonly IChannelManager channelManager;
        private readonly ITaskManager taskManager;
        private readonly IApplicationPaths appPaths;
        private readonly ILogger logger;

        public RadarrChannelSyncTask(
            RadarrClient radarrClient,
            IChannelManager channelManager,
            ITaskManager taskManager,
            IApplicationPaths appPaths,
            ILogger logger)
        {
            this.radarrClient = radarrClient;
            this.channelManager = channelManager;
            this.taskManager = taskManager;
            this.appPaths = appPaths;
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
        }
    }
}