// ASSUMPTIONS FLAGGED FOR REVIEW — see RadarrComingSoonChannel.cs header for
// the shared list (ManageComingSoonPlugin.Instance.Configuration, cache file location, etc).
// Additional ones specific to this file:
//   6. IScheduledTask / TaskTriggerInfo shape (property names, Execute
//      signature) is written to the most common Emby plugin SDK pattern but
//      not verified against this project's actual SDK version — confirm it
//      compiles and adjust member names if the installed SDK differs.
//   7. IChannelManager is assumed constructor-injectable via Emby's DI,
//      matching how RadarrClient/TmdbService are already used elsewhere.

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
    using MediaBrowser.Controller.Channels;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Tasks;

    public class RadarrChannelSyncTask : IScheduledTask
    {
        private readonly RadarrClient radarrClient;
        private readonly RadarrComingSoonChannel channel;
        private readonly IChannelManager channelManager;
        private readonly ILogger logger;

        public RadarrChannelSyncTask(
            RadarrClient radarrClient,
            RadarrComingSoonChannel channel,
            IChannelManager channelManager,
            ILogger logger)
        {
            this.radarrClient = radarrClient;
            this.channel = channel;
            this.channelManager = channelManager;
            this.logger = logger;
        }

        public string Name => "Sync Radarr Coming Soon Channel";

        public string Key => "ManageComingSoon-RadarrChannelSync";

        public string Description =>
            "Queries Radarr for monitored, not-yet-downloaded movies and updates the Radarr Coming Soon channel's cache. Only runs when Radarr integration is enabled and sync mode is set to Cached.";

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

            cache.Items = newItems;
            cache.LastSyncSucceeded = true;
            cache.LastSyncUtc = DateTimeOffset.UtcNow;
            channel.WriteCache(cache);

            // Whether removed items actually disappear from the channel's
            // Emby-side listing (and whether CanDelete/DeleteItem on the
            // channel get invoked as part of this) is exactly the open
            // question RadarrRemovalStrategy exists to help answer. This
            // task doesn't call DeleteItem itself — see the summary note on
            // that in chat for why, and what remains genuinely unconfirmed.
            await channelManager
                .RefreshChannelContent(channel, maxRefreshLevel: 0, restrictTopLevelFolderId: null, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}