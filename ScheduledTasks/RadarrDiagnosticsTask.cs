namespace ManageComingSoon.ScheduledTasks
{
    using ManageComingSoon.Channels;
    using ManageComingSoon.Model;
    using MediaBrowser.Controller.Channels;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Tasks;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public class RadarrDiagnosticsTask : IScheduledTask
    {
        private const string RefreshChannelsTaskKey = "RefreshInternetChannels";
        private const string RefreshChannelsTaskName = "Refresh Internet Channels";

        private readonly ILibraryManager libraryManager;
        private readonly IChannelManager channelManager;
        private readonly ITaskManager taskManager;
        private readonly ILogger logger;

        public RadarrDiagnosticsTask(
            ILibraryManager libraryManager,
            IChannelManager channelManager,
            ITaskManager taskManager,
            ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.channelManager = channelManager;
            this.taskManager = taskManager;
            this.logger = logger;
        }

        public string Name => "Radarr Channel Diagnostics (manual)";

        public string Key => "ManageComingSoon-RadarrDiagnostics";

        public string Description =>
            "Manual-only diagnostic/repair tool. Empties the Radarr Coming Soon channel's contents and deletes the Channel entity from the database. Does NOT trigger 'Refresh Internet Channels' afterwards, so the channel stays gone from the UI until that task next runs on its own schedule (or manually).";

        public string Category => "Manage Coming Soon";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield break;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = ManageComingSoonPlugin.Instance.Configuration;

            LogChannelItemInventory(config);

            await EmptyAndDeleteChannel(config, cancellationToken).ConfigureAwait(false);
        }

        private void LogChannelItemInventory(PluginConfiguration config)
        {
            var channelBaseItem = FindChannelBaseItem(config.RadarrChannelName);

            if (channelBaseItem == null)
            {
                logger.Warn("ManageComingSoon Diagnostics: Could not find persisted Channel BaseItem '{0}'.", config.RadarrChannelName);
                return;
            }

            var movieItems = libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie" },
                Parent = channelBaseItem
            }).Items;

            logger.Info(
                "ManageComingSoon Diagnostics: {0} Movie item(s) found under channel '{1}'.",
                movieItems.Length, channelBaseItem.Name);

            foreach (var movieItem in movieItems)
            {
                var providerIdsDesc = movieItem.ProviderIds == null || movieItem.ProviderIds.Count == 0
                    ? "(none)"
                    : string.Join("; ", movieItem.ProviderIds.Select(kv => string.Format("{0}={1}", kv.Key, kv.Value)));

                logger.Info(
                    "ManageComingSoon Diagnostics:   Emby item — InternalId={0}, Name='{1}', ProviderIds=[{2}].",
                    movieItem.InternalId, movieItem.Name, providerIdsDesc);
            }
        }

        // -----------------------------------------------------------------
        // Confirmed behaviour (see Evidence.md for the full writeup):
        //   - IChannelManager.DeleteItem(BaseItem) is for channel CONTENT
        //     items (routes to ISupportsDelete on the owning channel) — not
        //     applicable to the Channel entity itself.
        //   - ILibraryManager.DeleteItem(item, options) is the real generic
        //     BaseItem-removal path and does remove a Channel row.
        //   - The row does NOT survive the next run of Emby's own built-in
        //     "Refresh Internet Channels" task — confirmed via direct test.
        //     That task runs independently of RadarrEnabled and re-registers
        //     a row for every currently-exported IChannel, ours included.
        //   - This diagnostic run deliberately does NOT trigger that task
        //     itself after the delete, so the channel stays gone until it
        //     next runs on its own schedule or is triggered manually.
        //
        // RadarrEnabled is flipped in-memory only (never persisted) and
        // restored in a finally block. Setting it false first empties the
        // channel's contents via the one refresh below, before the row
        // itself is deleted.
        // -----------------------------------------------------------------

        private async Task EmptyAndDeleteChannel(PluginConfiguration config, CancellationToken cancellationToken)
        {
            bool originalEnabled = config.RadarrEnabled;

            try
            {
                logger.Info("ManageComingSoon Diagnostics: --- Starting empty + delete ---");

                var channelBaseItemBefore = FindChannelBaseItem(config.RadarrChannelName);
                if (channelBaseItemBefore == null)
                {
                    logger.Warn("ManageComingSoon Diagnostics: No persisted Channel BaseItem found — nothing to do.");
                    return;
                }

                int countBefore = CountChannelMovies(channelBaseItemBefore);
                logger.Info("ManageComingSoon Diagnostics: Step 1 — {0} Movie item(s) under channel before.", countBefore);

                // --- Step 2: empty the channel's contents ---
                config.RadarrEnabled = false;
                logger.Info("ManageComingSoon Diagnostics: Step 2 — RadarrEnabled set to false (in-memory only, not persisted). GetChannelItems will now return an empty list.");

                await TriggerRefreshInternetChannels().ConfigureAwait(false);

                var channelBaseItemAfterEmpty = FindChannelBaseItem(config.RadarrChannelName);
                int countAfterEmpty = channelBaseItemAfterEmpty == null ? -1 : CountChannelMovies(channelBaseItemAfterEmpty);
                logger.Info(
                    "ManageComingSoon Diagnostics: Step 2 result — {0} Movie item(s) under channel after empty+refresh.",
                    countAfterEmpty);

                // --- Step 3: delete the Channel entity — no further refresh after this ---
                if (channelBaseItemAfterEmpty != null)
                {
                    logger.Info("ManageComingSoon Diagnostics: Step 3 — calling ILibraryManager.DeleteItem against the Channel BaseItem (DeleteFileLocation=false).");

                    try
                    {
                        libraryManager.DeleteItem(
                            channelBaseItemAfterEmpty,
                            new DeleteOptions { DeleteFileLocation = false });

                        logger.Info("ManageComingSoon Diagnostics: Step 3 — DeleteItem call completed without throwing.");
                    }
                    catch (Exception ex)
                    {
                        logger.ErrorException("ManageComingSoon Diagnostics: Step 3 — DeleteItem threw", ex);
                    }

                    var channelBaseItemAfterDelete = FindChannelBaseItem(config.RadarrChannelName);
                    logger.Info(
                        "ManageComingSoon Diagnostics: Step 3 result — Channel BaseItem {0} after DeleteItem attempt.",
                        channelBaseItemAfterDelete == null ? "IS GONE (found null)" : "STILL PRESENT (DeleteItem did not remove it)");
                }

                logger.Info("ManageComingSoon Diagnostics: --- Complete. No further 'Refresh Internet Channels' run triggered — the channel will stay gone from the UI until that task next runs on its own schedule, or is triggered manually from Scheduled Tasks. ---");
            }
            finally
            {
                // RadarrEnabled is restored here, but note: this alone does
                // NOT bring the channel back — nothing after this point
                // triggers a refresh, by design.
                config.RadarrEnabled = originalEnabled;
            }
        }

        private BaseItem FindChannelBaseItem(string channelName)
        {
            return libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Channel" },
                Name = channelName
            }).Items.FirstOrDefault();
        }

        private int CountChannelMovies(BaseItem channelBaseItem)
        {
            return libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie" },
                Parent = channelBaseItem
            }).Items.Length;
        }

        private async Task TriggerRefreshInternetChannels()
        {
            var refreshWorker = taskManager.ScheduledTasks
                .FirstOrDefault(w => string.Equals(w.ScheduledTask?.Key, RefreshChannelsTaskKey, StringComparison.OrdinalIgnoreCase))
                ?? taskManager.ScheduledTasks
                    .FirstOrDefault(w => string.Equals(w.Name, RefreshChannelsTaskName, StringComparison.OrdinalIgnoreCase));

            if (refreshWorker == null)
            {
                logger.Warn(
                    "ManageComingSoon Diagnostics: Could not find the built-in channel-refresh task (Key='{0}' / Name='{1}').",
                    RefreshChannelsTaskKey, RefreshChannelsTaskName);
                return;
            }

            await taskManager.Execute(refreshWorker, new TaskOptions()).ConfigureAwait(false);
        }
    }
}